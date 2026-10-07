using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EpubKindleFix;

public sealed class LibraryBook
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourcePath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "待识别作者";
    public string Category { get; set; } = "未分类";
    public string CoverPath { get; set; } = "";
    public bool Favorite { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? SentAt { get; set; }
    public string State { get; set; } = "待检查";
    public string Error { get; set; } = "";
    public int Attempts { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public bool PreviewApproved { get; set; }
    public string DeliveryChannel { get; set; } = "邮件";
    public EpubRepairReport? Report { get; set; }
    public List<DeliveryRecord> Deliveries { get; set; } = [];
    [JsonIgnore]
    public int Progress => State switch { "待检查" => 0, "检查中" => 20, "待发送" or "等待预览" or "等待网页发送" or "已跳过" => 65, "发送中" => 85, "邮件已提交" or "网页已提交" => 100, _ => Report is null ? 20 : 65 };
    [JsonIgnore]
    public string Detail => Error.Length > 0 ? Error + (RetryAt is null ? "" : $" · {RetryAt.Value.ToLocalTime():HH:mm:ss} 自动重试")
        : SentAt is not null ? $"{DeliveryChannel}提交于 {SentAt.Value.ToLocalTime():MM-dd HH:mm} · 等待 Kindle 转换与联网同步"
        : Report is null ? $"加入于 {AddedAt.ToLocalTime():MM-dd HH:mm}" : $"已检查 {Report.ImagesFound} 张图片 · 补齐 {Report.ManifestEntriesAdded} 项";
    [JsonIgnore]
    public string PreviewPath => Report?.OutputPath ?? SourcePath;
}

public sealed record DeliveryRecord(DateTimeOffset At, string Recipient, string Result);

public sealed class LibraryData
{
    public List<LibraryBook> Books { get; set; } = [];
    public bool Paused { get; set; }
}

public static class QingyueData
{
    public static string Root => @"G:\chatgpt\轻阅";
    public static string LegacyRoot => @"G:\chatgpt";
    public static string RelocateExisting(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        if (File.Exists(path)) return path;
        var prefix = LegacyRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return path;
        var moved = Path.Combine(Root, path[prefix.Length..]);
        return File.Exists(moved) ? moved : path;
    }
}

public sealed class PersonalLibrary
{
    public static string Folder => Path.Combine(QingyueData.Root, "轻阅数据", "书架");
    private static string FilePath => Path.Combine(Folder, "library.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Gate = new();
    private bool unreadable;
    public List<LibraryBook> Books { get; }
    public event Action? Changed;
    public bool Paused { get; set; }
    public string LoadNotice { get; private set; } = "";

    public PersonalLibrary()
    {
        Books = [];
        var found = false;
        var needsSave = false;
        var hadUnreadableFile = false;
        var canonicalUnreadable = false;
        var roots = new[] { Folder, Path.Combine(QingyueData.LegacyRoot, "轻阅数据", "书架") };
        foreach (var directory in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var primary = Path.Combine(directory, "library.json");
            LibraryData? data = null;
            var restoredBackup = false;
            foreach (var path in new[] { primary, primary + ".bak" })
            {
                if (!File.Exists(path)) continue;
                try
                {
                    data = JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(path));
                    if (data is null) throw new JsonException("书架文件为空。");
                    restoredBackup = path.EndsWith(".bak", StringComparison.OrdinalIgnoreCase);
                    break;
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    hadUnreadableFile = true;
                    if (path == FilePath) canonicalUnreadable = true;
                }
            }
            if (data is null) continue;
            if (!found) Paused = data.Paused;
            found = true;
            if (restoredBackup) needsSave = true;
            foreach (var book in data.Books ?? [])
            {
                if (book is null) continue;
                if (string.IsNullOrWhiteSpace(book.Id)) { book.Id = Guid.NewGuid().ToString("N"); needsSave = true; }
                if (Books.Any(existing => existing.Id == book.Id)) continue;
                Normalize(book, ref needsSave);
                Books.Add(book);
                if (directory != Folder) needsSave = true;
            }
        }
        Books.Sort((a, b) => b.AddedAt.CompareTo(a.AddedAt));
        unreadable = hadUnreadableFile && !found;
        if (unreadable)
            LoadNotice = "书架记录暂时无法读取，原文件已保留。为避免覆盖，暂不能保存新记录。请检查数据目录与备份。";
        else if (needsSave)
        {
            try
            {
                // Preserve an unreadable primary separately before replacing its backup.
                if (canonicalUnreadable && File.Exists(FilePath))
                    File.Copy(FilePath, Path.Combine(Folder, $"library-recovery-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json"));
                Save();
                LoadNotice = hadUnreadableFile ? "已从可用记录恢复书架，原记录已保留。" : "书架路径已恢复，之后重启会继续保留。";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { LoadNotice = "书架已读取，但恢复后的记录暂未保存：" + ex.Message; }
        }
    }

    private static void Normalize(LibraryBook book, ref bool changed)
    {
        book.Title ??= ""; book.Author ??= "待识别作者"; book.Category ??= "未分类";
        book.Error ??= ""; book.State ??= "待检查"; book.DeliveryChannel ??= "邮件";
        book.Deliveries ??= [];
        var source = QingyueData.RelocateExisting(book.SourcePath);
        var cover = QingyueData.RelocateExisting(book.CoverPath);
        if (source != book.SourcePath || cover != book.CoverPath) changed = true;
        book.SourcePath = source; book.CoverPath = cover;
        if (book.Report is { } report)
        {
            var output = QingyueData.RelocateExisting(report.OutputPath);
            if (output != report.OutputPath) { book.Report = report with { OutputPath = output }; changed = true; }
        }
        if (book.State == "检查中") { book.State = "待检查"; changed = true; }
        // An interrupted delivery must never be automatically sent twice.
        if (book.State == "发送中")
        {
            book.State = "发送结果待确认";
            book.Error = "上次发送期间程序退出，请先确认 Kindle 是否已收到，再决定是否重试。";
            book.RetryAt = null; changed = true;
        }
    }

    public void Save()
    {
        if (unreadable) throw new IOException("书架原记录和备份无法读取，已阻止空书架覆盖。请先恢复记录。");
        lock (Gate)
        {
            Directory.CreateDirectory(Folder);
            var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new LibraryData { Books = Books, Paused = Paused }, Json);
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes); stream.Flush(flushToDisk: true); }
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak", ignoreMetadataErrors: true);
                else File.Move(temp, FilePath);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        Changed?.Invoke();
    }

    public LibraryBook Add(string path)
    {
        var book = new LibraryBook { SourcePath = path, Title = Path.GetFileNameWithoutExtension(path) };
        Books.Insert(0, book);
        try { Save(); }
        catch { Books.Remove(book); throw; }
        return book;
    }
    public void Update(LibraryBook book, string state, string error = "")
    {
        book.State = state; book.Error = error;
        Save();
    }
}
