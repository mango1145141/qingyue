using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace EpubKindleFix;

public sealed record ReadingSearch(string Query, DateTimeOffset At, bool FromRecommendation = false)
{
    public string Kind { get; init; } = "book";
    public string DisplayDate => At.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm");
}

public sealed class ReadingProfile
{
    public bool RememberSearches { get; set; } = true;
    public List<ReadingSearch> Searches { get; set; } = new();
    public List<string> DismissedBooks { get; set; } = new();
    public List<DiscoveryBook> WantToRead { get; set; } = new();
    public List<string> FavoriteGenres { get; set; } = new();
    public List<string> ExcludedGenres { get; set; } = new();
    public string DiscoveryMode { get; set; } = "均衡";
    public bool ChinesePriority { get; set; } = true;
}

public static class ReadingProfileStore
{
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "KindleBookRepair", "reading-profile.dat");

    public static ReadingProfile Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var data = JsonSerializer.Deserialize<ReadingProfile>(Dpapi.Unprotect(File.ReadAllBytes(FilePath))) ?? new();
            data.Searches = (data.Searches ?? new()).Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Query)
                && s.Query.Length <= 300).OrderByDescending(s => s.At).Take(500).ToList();
            data.DismissedBooks = (data.DismissedBooks ?? new()).Distinct().Take(500).ToList();
            data.WantToRead = (data.WantToRead ?? []).Where(b => b is not null).DistinctBy(b => b.Id).ToList();
            data.FavoriteGenres ??= []; data.ExcludedGenres ??= [];
            return data;
        }
        catch { return new(); }
    }

    public static void Save(ReadingProfile profile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, Dpapi.Protect(JsonSerializer.Serialize(profile)));
        File.Move(temp, FilePath, overwrite: true);
    }
}

public sealed record DiscoveryBook(string Id, string Title, string Author, string[] Genres,
    string Description, string[] Aliases, string SourceUrl)
{
    public string GenreLabel => string.Join(" · ", Genres.Take(2));
    public long CoverId { get; init; }
    public string? WorkKey { get; init; }
    public string Kind { get; init; } = "book";
    public bool IsComic => Kind == "comic" || Genres.Contains("漫画");
}
public sealed record RecommendedBook(DiscoveryBook Book, string Reason);
public sealed record DiscoveryResult(string Summary, IReadOnlyList<RecommendedBook> Books)
{
    public string[] PreferredGenres { get; init; } = [];
}

public static class ReadingDiscovery
{
    private static readonly Dictionary<string, string[]> GenreKeywords = new()
    {
        ["漫画"] = ["漫画", "漫畫", "manga", "comic", "graphicnovel", "少年漫", "少女漫"],
        ["日常治愈"] = ["日常", "治愈", "治癒", "温馨", "溫馨", "sliceoflife"],
        ["运动竞技"] = ["运动", "運動", "竞技", "競技", "篮球", "籃球", "排球", "足球", "sports"],
        ["恋爱青春"] = ["恋爱", "戀愛", "青春", "爱情", "愛情", "校园", "校園", "romance"],
        ["轻小说"] = ["轻小说", "輕小說", "ライトノベル", "lightnovel", "电击文库", "角川", "异世界", "異世界", "勇者", "转生", "轉生"],
        ["科幻"] = ["科幻", "sciencefiction", "宇宙", "机器人", "機器人", "外星", "太空", "时间旅行"],
        ["悬疑推理"] = ["悬疑", "懸疑", "推理", "侦探", "偵探", "mystery", "detective", "凶手", "谋杀", "謀殺"],
        ["幻想冒险"] = ["奇幻", "魔法", "冒险", "冒險", "fantasy", "异世界", "異世界", "勇者", "魔王", "转生", "轉生"],
        ["文学小说"] = ["文学", "文學", "literature", "经典小说", "文艺", "文藝"],
        ["历史"] = ["历史", "歷史", "history", "明朝", "宋朝", "汉朝", "唐朝", "三国", "史记"],
        ["哲学思考"] = ["哲学", "哲學", "philosophy", "伦理", "倫理", "斯多葛", "存在主义", "存在主義"],
        ["心理与成长"] = ["心理", "psychology", "成长", "成長", "习惯", "習慣", "自律", "专注", "專注", "效率", "学习方法"],
        ["自然科普"] = ["科普", "science", "物理", "数学", "數學", "天文", "生物", "自然", "进化", "進化"],
    };

    public static string Normalize(string value) => string.Concat(value.Normalize(NormalizationForm.FormKC)
        .Where(char.IsLetterOrDigit)).ToLowerInvariant();

    public static bool TitleMatches(DiscoveryBook book, string query)
    {
        var value = Normalize(query);
        return new[] { book.Title }.Concat(book.Aliases.Where(a => !Normalize(book.Author).Contains(Normalize(a), StringComparison.Ordinal))).Select(Normalize)
            .Any(title => title.Length >= 2 && value.Contains(title, StringComparison.Ordinal));
    }

    private static bool AuthorMatches(DiscoveryBook book, string query)
    {
        var author = Normalize(book.Author);
        return author.Length >= 2 && query.Contains(author, StringComparison.Ordinal)
            || book.Aliases.Select(Normalize).Any(a => a.Length >= 2 && (author.Contains(a, StringComparison.Ordinal)
                || !a.Any(ch => ch > 127)) && query.Contains(a, StringComparison.Ordinal));
    }

    public static DiscoveryResult Recommend(ReadingProfile profile, DateOnly date, int page = 0,
        IReadOnlySet<string>? excluded = null)
    {
        var now = DateTimeOffset.UtcNow;
        var history = profile.RememberSearches ? profile.Searches.Where(s => s.At <= now.AddMinutes(5)
            && now - s.At < TimeSpan.FromDays(180)).OrderByDescending(s => s.At)
            .GroupBy(s => (Normalize(s.Query), s.Kind, DateOnly.FromDateTime(s.At.ToOffset(TimeSpan.FromHours(8)).DateTime)))
            .Select(g => g.First()).ToList() : new List<ReadingSearch>();
        var genreScores = GenreKeywords.Keys.ToDictionary(g => g, _ => 0d);
        var authorScores = new Dictionary<string, double>();
        foreach (var search in history)
        {
            var query = Normalize(search.Query);
            var weight = Math.Pow(0.5, Math.Max(0, (now - search.At).TotalDays) / 30)
                * (search.FromRecommendation ? 0.35 : 1);
            var matches = ReadingCatalog.Books.Where(b => TitleMatches(b, search.Query)
                || AuthorMatches(b, query)).ToList();
            var foundGenres = matches.SelectMany(b => b.Genres).Distinct().ToHashSet();
            if (search.Kind == "comic") foundGenres.Add("漫画");
            foreach (var genre in GenreKeywords)
                if (genre.Value.Select(Normalize).Any(keyword => query.Contains(keyword, StringComparison.Ordinal))) foundGenres.Add(genre.Key);
            foreach (var genre in foundGenres) if (genreScores.ContainsKey(genre)) genreScores[genre] += weight;
            foreach (var author in matches.Select(b => b.Author).Distinct())
                authorScores[author] = authorScores.GetValueOrDefault(author) + weight;
        }
        foreach (var wish in profile.WantToRead)
        {
            foreach (var genre in wish.Genres) if (genreScores.ContainsKey(genre)) genreScores[genre] += 1.5;
            authorScores[wish.Author] = authorScores.GetValueOrDefault(wish.Author) + 0.5;
        }
        foreach (var genre in profile.FavoriteGenres) if (genreScores.ContainsKey(genre)) genreScores[genre] += 3;
        foreach (var genre in profile.ExcludedGenres) genreScores.Remove(genre);
        var top = genreScores.Where(g => g.Value > 0.05).OrderByDescending(g => g.Value).Take(3).Select(g => g.Key).ToList();
        var personalized = top.Count > 0;
        var summary = !profile.RememberSearches ? "搜索记录已暂停 · 为你精选不同类型的书"
            : history.Count == 0 ? "从下一次搜索开始了解你 · 先看看这些精选好书"
            : !personalized ? "还没判断出你的题材偏好 · 先看看不同类型的书"
            : "根据近期搜索，你可能喜欢：" + string.Join("、", top);
        if (personalized && (profile.WantToRead.Count > 0 || profile.FavoriteGenres.Count > 0))
            summary = "结合想读清单与题材偏好，为你推荐：" + string.Join("、", top);
        summary += " · " + profile.DiscoveryMode;
        var available = ReadingCatalog.Books.Where(b => !profile.DismissedBooks.Contains(b.Id)
            && !b.Genres.Any(profile.ExcludedGenres.Contains) && !profile.WantToRead.Any(w => w.Id == b.Id)
            && !(excluded?.Contains(b.Id) ?? false)).ToList();
        var pool = available.Where(b =>
            !history.Any(s => TitleMatches(b, s.Query))).ToList();
        if (pool.Count == 0) pool = available;
        if (pool.Count == 0) return new(summary, Array.Empty<RecommendedBook>()) { PreferredGenres = top.ToArray() };
        double Score(DiscoveryBook book) => book.Genres.Sum(g => genreScores.GetValueOrDefault(g))
            + authorScores.GetValueOrDefault(book.Author) * 0.6;
        var ordered = pool.OrderByDescending(Score).ThenBy(b => StableOrder(b.Id, date.DayNumber)).ToList();
        const int shelfSize = 12;
        var focusedCount = profile.DiscoveryMode == "熟悉" ? 10 : profile.DiscoveryMode == "探索" ? 4 : 8;
        // Prefer eight relevant books and reserve room for discovery.
        var focused = personalized ? ordered.Where(b => Score(b) > 0.01).ToList() : ordered;
        var picked = new List<DiscoveryBook>();
        var start = Math.Abs((long)page) * focusedCount;
        for (var i = 0; i < focused.Count && picked.Count < focusedCount; i++)
        {
            var next = focused[(int)((start + i) % focused.Count)];
            if (!personalized && picked.Any(b => b.Genres[0] == next.Genres[0])) continue;
            if (picked.Count(b => b.Author == next.Author) >= 2) continue;
            picked.Add(next);
        }
        var discovery = ordered.Where(b => !picked.Contains(b)
            && (personalized ? !b.Genres.Contains(top[0]) : !picked.Any(p => p.Genres[0] == b.Genres[0]))).ToList();
        if (discovery.Count == 0) discovery = ordered.Where(b => !picked.Contains(b)).ToList();
        void FillFrom(IReadOnlyList<DiscoveryBook> candidates, bool limitAuthor)
        {
            if (candidates.Count == 0) return;
            for (var i = 0; i < candidates.Count && picked.Count < shelfSize; i++)
            {
                var next = candidates[(int)((Math.Abs((long)page) * 4 + i) % candidates.Count)];
                if (picked.Contains(next) || limitAuthor && (picked.Count(b => b.Author == next.Author) >= 2
                    || !personalized && picked.Count(b => b.Genres[0] == next.Genres[0]) >= 2)) continue;
                picked.Add(next);
            }
        }
        FillFrom(discovery, true);
        FillFrom(ordered, true);
        FillFrom(ordered, false);
        // Searches can exhaust unseen titles; explicitly dismissed books stay excluded.
        FillFrom(available
            .OrderByDescending(Score).ThenBy(b => StableOrder(b.Id, date.DayNumber)).ToList(), false);
        // Reserve a small discovery share for comics, using the same score and
        // explicit exclusions. Comic preferences can naturally rank more titles.
        if (!profile.ExcludedGenres.Contains("漫画"))
        {
            foreach (var comic in ordered.Where(b => b.IsComic && !picked.Contains(b)))
            {
                if (picked.Count(b => b.IsComic) >= 2) break;
                var replace = picked.LastOrDefault(b => !b.IsComic);
                if (picked.Count >= shelfSize && replace is null) break;
                if (picked.Count >= shelfSize) picked.Remove(replace!);
                picked.Add(comic);
            }
        }
        return new(summary, picked.Select(b =>
        {
            var matchGenre = b.Genres.OrderByDescending(g => genreScores.GetValueOrDefault(g)).First();
            var evidence = history.FirstOrDefault(s => (TitleMatchesAnyGenre(s.Query, matchGenre)));
            var reason = authorScores.GetValueOrDefault(b.Author) > 0.05
                ? "你搜过这位作者或其作品，可继续读读同作者的书"
                : personalized && genreScores.GetValueOrDefault(matchGenre) > 0.05
                    ? evidence is null ? "与你近期搜索的「" + matchGenre + "」题材相近"
                        : "因为你搜过「" + ShortQuery(evidence.Query) + "」，试试同类的「" + matchGenre + "」"
                    : "换一种阅读口味，发现「" + matchGenre + "」";
            return new RecommendedBook(b, reason);
        }).ToList()) { PreferredGenres = top.ToArray() };
    }

    private static bool TitleMatchesAnyGenre(string query, string genre)
    {
        var normalized = Normalize(query);
        return GenreKeywords[genre].Select(Normalize).Any(k => normalized.Contains(k, StringComparison.Ordinal))
            || ReadingCatalog.Books.Any(b => b.Genres.Contains(genre) && (TitleMatches(b, query)
                || AuthorMatches(b, normalized)));
    }
    private static string ShortQuery(string query) => new StringInfo(query).LengthInTextElements > 16
        ? new StringInfo(query).SubstringByTextElements(0, 16) + "…" : query;
    private static uint StableOrder(string id, int day)
    {
        uint value = unchecked((uint)day * 2654435761u);
        foreach (var ch in id) value = unchecked((value ^ ch) * 16777619u);
        return value;
    }
}

public sealed record ReadingQuote(string Text, string Attribution, string Url);
public static class DailyReadingQuote
{
    public static DateOnly Today => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).DateTime);
    private static readonly ReadingQuote[] Quotes =
    [
        new("学而时习之，不亦说乎？", "孔子 ·《论语·学而》", "https://ctext.org/analects/xue-er/zh"),
        new("温故而知新，可以为师矣。", "孔子 ·《论语·为政》", "https://ctext.org/analects/wei-zheng/zh"),
        new("学而不思则罔，思而不学则殆。", "孔子 ·《论语·为政》", "https://ctext.org/analects/wei-zheng/zh"),
        new("知之为知之，不知为不知，是知也。", "孔子 ·《论语·为政》", "https://ctext.org/analects/wei-zheng/zh"),
        new("学而不厌，诲人不倦。", "孔子 ·《论语·述而》节选", "https://ctext.org/lunyu-zhushu/shu-er"),
        new("学不可以已。", "荀子 ·《荀子·劝学》", "https://ctext.org/xunzi/quan-xue"),
        new("青，取之于蓝，而青于蓝。", "荀子 ·《荀子·劝学》", "https://ctext.org/xunzi/quan-xue"),
        new("锲而不舍，金石可镂。", "荀子 ·《荀子·劝学》", "https://ctext.org/xunzi/quan-xue"),
        new("不积跬步，无以至千里；不积小流，无以成江海。", "荀子 ·《荀子·劝学》节选", "https://ctext.org/xunzi/quan-xue"),
        new("千里之行，始于足下。", "老子 ·《道德经》第六十四章", "https://ctext.org/dao-de-jing/zh"),
        new("知人者智，自知者明。", "老子 ·《道德经》第三十三章", "https://ctext.org/dao-de-jing/zh"),
        new("上善若水。", "老子 ·《道德经》第八章", "https://ctext.org/dao-de-jing"),
        new("知者不言，言者不知。", "老子 ·《道德经》第五十六章", "https://ctext.org/dao-de-jing"),
        new("不登高山，不知天之高也。", "荀子 ·《荀子·劝学》节选", "https://ctext.org/xunzi/quan-xue"),
        new("不临深溪，不知地之厚也。", "荀子 ·《荀子·劝学》节选", "https://ctext.org/xunzi/quan-xue"),
        new("读书破万卷，下笔如有神。", "杜甫 ·《奉赠韦左丞丈二十二韵》", "https://dict.revised.moe.edu.tw/dictView.jsp?ID=47463&q=1&word=%E8%AE%80%E6%9B%B8"),
        new("问渠那得清如许？为有源头活水来。", "朱熹 ·《观书有感二首·其一》", "https://www.shidianguji.com/zh/mingju/7624424286379982888"),
        new("奇文共欣赏，疑义相与析。", "陶渊明 ·《移居二首·其一》", "https://dict.variants.moe.edu.tw/dictView.jsp?ID=43234"),
        new("纸上得来终觉浅，绝知此事要躬行。", "陆游 ·《冬夜读书示子聿》", "https://www.tcps.ntpc.edu.tw/var/file/0/1000/img/135/573725261.pdf"),
    ];
    public static ReadingQuote ForDay(DateOnly day) => Quotes[day.DayNumber % Quotes.Length];
}
