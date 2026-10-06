using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EpubKindleFix;

public sealed record RecommendationBatch(IReadOnlyList<RecommendedBook> Books, bool HasMore,
    bool NeedsRetry = false);

/// <summary>A browsing session, with one cursor per topic and no repeated works.</summary>
public sealed class RecommendationFeed
{
    private static readonly Dictionary<string, string> Topics = new()
    {
        ["轻小说"] = "(subject:\"Light novels\" OR subject:\"ライトノベル\" OR subject:\"轻小说\")",
        ["科幻"] = "subject:\"Science fiction\"",
        ["悬疑推理"] = "subject:\"Detective and mystery stories\"",
        ["幻想冒险"] = "subject:\"Fantasy fiction\"",
        ["文学小说"] = "subject:Fiction",
        ["历史"] = "subject:History",
        ["哲学思考"] = "subject:Philosophy",
        ["心理与成长"] = "(subject:Psychology OR subject:\"Self-help\")",
        ["自然科普"] = "subject:Science",
        ["漫画"] = "(subject:Manga OR subject:\"漫画\" OR subject:\"Comics and graphic novels\")",
        ["日常治愈"] = "(subject:Manga OR subject:\"Graphic novels\") AND (subject:\"Everyday life\" OR subject:\"Slice of life\" OR subject:Friendship)",
        ["运动竞技"] = "(subject:Manga OR subject:\"Graphic novels\") AND (subject:Sports OR subject:Basketball OR subject:Volleyball OR subject:Soccer)",
        ["恋爱青春"] = "(subject:Manga OR subject:\"Graphic novels\") AND (subject:Romance OR subject:\"Love stories\" OR subject:\"High school students\")"
    };
    private sealed class TopicCursor(string genre, string query)
    {
        public string Genre { get; } = genre;
        public string Query { get; } = query;
        public int Offset { get; set; }
        public bool Exhausted { get; set; }
    }
    private readonly ReadingProfile profile;
    private readonly DateOnly date;
    private readonly int seed;
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly HashSet<string> seenTitles = new(StringComparer.Ordinal);
    private readonly Queue<RecommendedBook> pending = new();
    private readonly TopicCursor[] cursors;
    private readonly SemaphoreSlim gate = new(1);
    private readonly Func<string, string, int, CancellationToken, Task<CatalogPage>> search;
    private int topicIndex;
    public string Summary { get; private set; }
    public string[] PreferredGenres { get; private set; }

    public RecommendationFeed(ReadingProfile profile, DateOnly date, int seed = 0,
        Func<string, string, int, CancellationToken, Task<CatalogPage>>? search = null)
    {
        this.profile = profile;
        this.date = date;
        this.seed = seed;
        this.search = search ?? OpenLibraryCatalog.SearchAsync;
        var first = ReadingDiscovery.Recommend(profile, date, seed);
        Summary = first.Summary;
        PreferredGenres = first.PreferredGenres;
        // Only these general topic labels are sent to the catalogue, never search text or account data.
        var plan = PreferredGenres.Concat(Topics.Keys).Distinct().Where(g => Topics.ContainsKey(g) && !profile.ExcludedGenres.Contains(g)).ToArray();
        cursors = plan.Select(g => new TopicCursor(g, Topics[g] + (profile.ChinesePriority ? " language:chi" : ""))).ToArray();
    }

    public async Task<RecommendationBatch> NextAsync(CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation);
        var books = new List<RecommendedBook>(12);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var local = ReadingDiscovery.Recommend(profile, date, seed, seen);
            Summary = local.Summary; PreferredGenres = local.PreferredGenres;
            foreach (var book in local.Books)
            {
                if (Accept(book.Book)) books.Add(book);
                if (books.Count == 12) break;
            }
            var requests = 0;
            while (books.Count < 12)
            {
                while (pending.Count > 0 && books.Count < 12)
                {
                    var next = pending.Dequeue();
                    if (Accept(next.Book)) books.Add(next);
                }
                if (books.Count == 12 || cursors.All(c => c.Exhausted)) break;
                // Bounded work per scroll; a sparse page can be continued by the next user scroll.
                if (++requests > 6) break;
                var cursor = ChooseCursor();
                try
                {
                    var page = await search(cursor.Query, cursor.Genre,
                        cursor.Offset, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    cursor.Offset += page.Scanned;
                    cursor.Exhausted = cursor.Offset >= page.Total || page.Scanned == 0;
                    foreach (var book in page.Books)
                    {
                        if (ReadingCatalog.Books.Any(b => ReadingDiscovery.TitleMatches(b, book.Title)
                            || new[] { b.Title }.Concat(b.Aliases).Any(a => book.Aliases.Any(alias =>
                                ReadingDiscovery.Normalize(a) == ReadingDiscovery.Normalize(alias))))) continue;
                        var reason = PreferredGenres.Contains(cursor.Genre)
                            ? "根据你的阅读偏好，继续探索「" + cursor.Genre + "」。"
                            : "换一种阅读口味，发现「" + cursor.Genre + "」。";
                        pending.Enqueue(new(book, reason));
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
                {
                    return new(books, true, NeedsRetry: true);
                }
            }
            return new(books, pending.Count > 0 || cursors.Any(c => !c.Exhausted));
        }
        finally { gate.Release(); }
    }

    private TopicCursor ChooseCursor()
    {
        // Two preference pages for each discovery page, while all cursors continue forward.
        var cycle = profile.DiscoveryMode == "熟悉" ? 6 : profile.DiscoveryMode == "探索" ? 3 : 3;
        var focusedSlots = profile.DiscoveryMode == "熟悉" ? 5 : profile.DiscoveryMode == "探索" ? 1 : 2;
        if (PreferredGenres.Length > 0 && topicIndex % cycle < focusedSlots)
        {
            var favorite = cursors.Where(c => PreferredGenres.Contains(c.Genre) && !c.Exhausted).ToArray();
            if (favorite.Length > 0) return favorite[(topicIndex++ / cycle) % favorite.Length];
        }
        for (var i = 0; i < cursors.Length; i++)
        {
            var next = cursors[topicIndex++ % cursors.Length];
            if (!next.Exhausted) return next;
        }
        throw new InvalidOperationException("Catalogue exhausted");
    }

    private bool Accept(DiscoveryBook book)
    {
        if (seen.Contains(book.Id) || profile.DismissedBooks.Contains(book.Id) || book.Genres.Any(profile.ExcludedGenres.Contains)
            || profile.WantToRead.Any(w => w.Id == book.Id)) return false;
        var title = ReadingDiscovery.Normalize(book.Title);
        if (seenTitles.Contains(title)) { seen.Add(book.Id); return false; }
        seen.Add(book.Id);
        seenTitles.Add(title);
        return true;
    }
}

public sealed record CatalogPage(IReadOnlyList<DiscoveryBook> Books, int Scanned, int Total);

public static partial class OpenLibraryCatalog
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(22) };
    private static readonly SemaphoreSlim Requests = new(1);
    private static DateTimeOffset lastRequest;
    private static readonly string CacheFolder = Path.Combine("G:\\chatgpt", "轻阅数据", "推荐");
    private const int PageSize = 36;

    public static async Task<CatalogPage> SearchAsync(string query, string genre, int offset, CancellationToken cancellation)
    {
        const string fields = "key,title,author_name,cover_i,first_publish_year,subject,editions,editions.title,editions.cover_i";
        var url = "https://openlibrary.org/search.json?q=" + Uri.EscapeDataString(query)
            + (query.Contains("language:chi", StringComparison.Ordinal) ? "&lang=zh" : "")
            + "&limit=" + PageSize + "&offset=" + offset + "&fields=" + fields;
        return ParsePage(await FetchAsync(url, TimeSpan.FromDays(7), cancellation), genre);
    }

    public static CatalogPage ParsePage(string json, string genre)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid catalogue page");
        var total = (int)Math.Clamp(Math.Max(Number(root, "numFound"), Number(root, "num_found")), 0, int.MaxValue);
        if (!root.TryGetProperty("docs", out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new JsonException("Missing catalogue entries");
        var books = new List<DiscoveryBook>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var key = Text(entry, "key");
            if (!WorkKeyPattern().IsMatch(key)) continue;
            var original = Text(entry, "title");
            var title = original;
            long cover = Number(entry, "cover_i");
            if (entry.TryGetProperty("editions", out var editions) && editions.ValueKind == JsonValueKind.Object && editions.TryGetProperty("docs", out var variants)
                && variants.ValueKind == JsonValueKind.Array && variants.GetArrayLength() > 0)
            {
                var edition = variants[0];
                var editionTitle = Text(edition, "title");
                if (editionTitle.Any(ch => ch is >= '\u3400' and <= '\u9fff')) title = editionTitle;
                if (Number(edition, "cover_i") > 0) cover = Number(edition, "cover_i");
            }
            var author = entry.TryGetProperty("author_name", out var authors) && authors.ValueKind == JsonValueKind.Array
                ? string.Join("、", authors.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()).Where(a => !string.IsNullOrWhiteSpace(a)).Take(3)) : "";
            // Skip malformed entries and missing covers rather than filling a scrolling shelf with blanks.
            if (string.IsNullOrWhiteSpace(title) || title.Length > 200 || author.Length == 0 || cover <= 0) continue;
            var year = Number(entry, "first_publish_year");
            var introduction = "「" + genre + "」书目" + (year is > 0 and < 2200 ? " · 首次出版于 " + year + " 年。" : "。")
                + "\n作品简介正在加载。";
            var comic = genre is "漫画" or "日常治愈" or "运动竞技" or "恋爱青春"
                || entry.TryGetProperty("subject", out var subjects) && subjects.ValueKind == JsonValueKind.Array
                    && subjects.EnumerateArray().Any(s => s.ValueKind == JsonValueKind.String && IsComicSubject(s.GetString()!));
            var genres = comic && genre != "漫画" ? new[] { "漫画", genre } : new[] { genre };
            books.Add(new DiscoveryBook("ol:" + key[7..], title, author, genres, introduction,
                [original], "https://openlibrary.org" + key) { CoverId = cover, WorkKey = key, Kind = comic ? "comic" : "book" });
        }
        return new(books, entries.GetArrayLength(), total);
    }

    public static async Task<string?> DescriptionAsync(string workKey, CancellationToken cancellation = default)
    {
        if (!WorkKeyPattern().IsMatch(workKey)) return null;
        using var document = JsonDocument.Parse(await FetchAsync("https://openlibrary.org" + workKey + ".json",
            TimeSpan.FromDays(30), cancellation));
        if (!document.RootElement.TryGetProperty("description", out var description)) return null;
        var value = description.ValueKind == JsonValueKind.String ? description.GetString()
            : description.ValueKind == JsonValueKind.Object ? Text(description, "value") : null;
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = LinkPattern().Replace(value, "$1");
        value = HtmlPattern().Replace(value, "");
        value = System.Net.WebUtility.HtmlDecode(value).Trim();
        return value.Length > 1800 ? value[..1800] + "…" : value;
    }

    private static async Task<string> FetchAsync(string url, TimeSpan age, CancellationToken cancellation)
    {
        var path = Path.Combine(CacheFolder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".json");
        try
        {
            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < age)
                return await File.ReadAllTextAsync(path, cancellation);
        }
        catch (IOException) { }
        await Requests.WaitAsync(cancellation);
        try
        {
            var delay = TimeSpan.FromSeconds(1.1) - (DateTimeOffset.UtcNow - lastRequest);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellation);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("QingyueDesktop/1.9.0 (+https://openlibrary.org/dev/docs/api)");
            lastRequest = DateTimeOffset.UtcNow;
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new IOException("Catalogue response too large");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellation)) > 0)
            {
                if (buffer.Length + read > 4 * 1024 * 1024) throw new IOException("Catalogue response too large");
                buffer.Write(chunk, 0, read);
            }
            var json = Encoding.UTF8.GetString(buffer.ToArray());
            using (JsonDocument.Parse(json)) { }
            try
            {
                Directory.CreateDirectory(CacheFolder);
                await File.WriteAllTextAsync(path + ".tmp", json, cancellation);
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return json;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            // Previously viewed catalogue pages remain useful when offline.
            try { if (File.Exists(path)) return await File.ReadAllTextAsync(path, cancellation); }
            catch (IOException) { }
            throw;
        }
        finally { Requests.Release(); }
    }

    private static string Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static long Number(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
    private static bool IsComicSubject(string value) => value.Contains("manga", StringComparison.OrdinalIgnoreCase)
        || value.Contains("graphic novel", StringComparison.OrdinalIgnoreCase) || value.Contains("comics", StringComparison.OrdinalIgnoreCase)
        || value.Contains("comic book", StringComparison.OrdinalIgnoreCase) || value.Contains("漫画") || value.Contains("漫畫");
    [GeneratedRegex(@"^/works/OL[0-9]+W$")] private static partial Regex WorkKeyPattern();
    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")] private static partial Regex LinkPattern();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex HtmlPattern();
}
