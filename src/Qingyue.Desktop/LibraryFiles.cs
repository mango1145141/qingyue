using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace EpubKindleFix;

public static class LibraryFiles
{
    public const string DefaultWebsiteUrl = "https://zh.z-library.sk/";
    public static string DownloadDirectory => Path.Combine(QingyueData.Root, "书籍下载");

    public static bool IsRetiredWebsite(Uri uri) => uri.Host.Equals("z-lib.ag", StringComparison.OrdinalIgnoreCase)
        || uri.Host.EndsWith(".z-lib.ag", StringComparison.OrdinalIgnoreCase);

    public static string MigrateWebsiteUrl(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return DefaultWebsiteUrl;
        var text = address.Trim();
        if (!text.Contains("://")) text = "https://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && IsRetiredWebsite(uri)
            ? DefaultWebsiteUrl : address;
    }

    public static bool TryWebsite(string text, out Uri? website)
    {
        website = null;
        text = text.Trim();
        if (!text.Contains("://")) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(parsed.Host)
            || !string.IsNullOrEmpty(parsed.UserInfo) || !parsed.IsDefaultPort
            || Uri.CheckHostName(parsed.Host) != UriHostNameType.Dns || IsRetiredWebsite(parsed)) return false;
        website = new Uri(parsed.GetLeftPart(UriPartial.Authority) + "/");
        return true;
    }

    public static bool IsLoginStatusPage(Uri uri) => uri.AbsolutePath.TrimEnd('/').Equals(
        "/api/auth-status", StringComparison.OrdinalIgnoreCase);

    public static string ReserveDownload(string suggestedName, string mimeType)
    {
        var name = suggestedName.Replace('\\', '/').Split('/').Last();
        name = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c) ? '_' : c).ToArray());
        if (!string.Equals(Path.GetExtension(name), ".epub", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(mimeType.Split(';')[0].Trim(), "application/epub+zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("请在搜索结果中选择 EPUB 版本。这种格式暂时不能自动修复和推送。");
            name = Path.GetFileNameWithoutExtension(name) + ".epub";
        }
        var stem = Path.GetFileNameWithoutExtension(name).Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(stem)) stem = "下载的书籍";
        if (Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)) stem = "书籍_" + stem;
        if (stem.Length > 100) stem = stem[..100];
        Directory.CreateDirectory(DownloadDirectory);
        var target = Path.Combine(DownloadDirectory, stem + "-" + Guid.NewGuid().ToString("N")[..10] + ".epub");
        // Reserve a unique destination before handing it to the browser.
        using var reserved = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        return target;
    }

    public static void ValidateDownloadedEpub(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidDataException("下载没有生成完整的书籍文件，请在网站中重新下载。");
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.GetEntry("META-INF/container.xml") is null)
                throw new InvalidDataException("下载结果不是有效的 EPUB，可能是网站提示页面。请重新选择 EPUB 版本。");
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("下载结果不是有效的 EPUB。请在网站中确认登录和下载状态后再试。");
        }
    }
}
