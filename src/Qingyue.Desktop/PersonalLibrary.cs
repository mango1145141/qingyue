using System.IO;
using System.Text.Json;

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
    public int Progress => State switch { "待检查" => 0, "检查中" => 20, "待发送" or "等待预览" or "等待网页发送" or "已跳过" => 65, "发送中" => 85, "邮件已提交" or "网页已提交" => 100, _ => Report is null ? 20 : 65 };
    public string Detail => Error.Length > 0 ? Error + (RetryAt is null ? "" : $" · {RetryAt.Value.ToLocalTime():HH:mm:ss} 自动重试")
        : SentAt is not null ? $"{DeliveryChannel}提交于 {SentAt.Value.ToLocalTime():MM-dd HH:mm} · 等待 Kindle 转换与联网同步"
        : Report is null ? $"加入于 {AddedAt.ToLocalTime():MM-dd HH:mm}" : $"已检查 {Report.ImagesFound} 张图片 · 补齐 {Report.ManifestEntriesAdded} 项";
    public string PreviewPath => Report?.OutputPath ?? SourcePath;
}

public sealed record DeliveryRecord(DateTimeOffset At, string Recipient, string Result);

public sealed class LibraryData
{
    public List<LibraryBook> Books { get; set; } = [];
    public bool Paused { get; set; }
}

public sealed class PersonalLibrary
{
    public static string Folder => Path.Combine(@"G:\chatgpt", "轻阅数据", "书架");
    private static string FilePath => Path.Combine(Folder, "library.json");
    public List<LibraryBook> Books { get; }
    public event Action? Changed;
    public bool Paused { get; set; }
    public PersonalLibrary()
    {
        LibraryData? data = null;
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            try { if (File.Exists(path)) { data = JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(path)); if (data is not null) break; } }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        Books = (data?.Books ?? []).Where(b => b is not null).ToList();
        Paused = data?.Paused ?? false;
        foreach (var b in Books)
        {
            b.Deliveries ??= [];
            if (b.State == "检查中") b.State = "待检查";
            // A process exit during delivery has an unknown result; never silently resend it.
            if (b.State == "发送中") { b.State = "发送结果待确认"; b.Error = "上次发送期间程序退出，请先确认 Kindle 是否已收到，再决定是否重试。"; b.RetryAt = null; }
        }
    }
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new LibraryData { Books = Books, Paused = Paused }, new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bak", true);
        File.Move(temp, FilePath, true);
        Changed?.Invoke();
    }
    public LibraryBook Add(string path)
    {
        var book = new LibraryBook { SourcePath = path, Title = Path.GetFileNameWithoutExtension(path) };
        Books.Insert(0, book);
        Save();
        return book;
    }
    public void Update(LibraryBook book, string state, string error = "")
    {
        book.State = state; book.Error = error;
        Save();
    }
}
