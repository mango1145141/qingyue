using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace EpubKindleFix;

public sealed record PreviewChapter(string Title, string ArchivePath);
public sealed record EpubDetails(string Title, string Author, string CoverPath, IReadOnlyList<PreviewChapter> Chapters);

public static partial class EpubPreview
{
    private static XDocument Xml(ZipArchiveEntry entry)
    {
        if (entry.Length > 4 * 1024 * 1024) throw new InvalidDataException("书籍目录过大，暂时无法预览。");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        return XDocument.Load(reader);
    }
    private static string? Resolve(string basePath, string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out _)) return null;
        try
        {
            var uri = new Uri(new Uri("https://epub.invalid/" + basePath), href);
            if (uri.Host != "epub.invalid") return null;
            return Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        }
        catch (UriFormatException) { return null; }
    }
    public static EpubDetails Read(string path, string id)
    {
        using var zip = ZipFile.OpenRead(path);
        var container = zip.GetEntry("META-INF/container.xml") ?? throw new InvalidDataException("EPUB 缺少目录信息。");
        var packagePath = Xml(container).Descendants().FirstOrDefault(x => x.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value;
        var package = zip.GetEntry(packagePath ?? "") ?? throw new InvalidDataException("EPUB 缺少书籍信息。");
        var doc = Xml(package);
        var title = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "title")?.Value.Trim();
        var author = string.Join("、", doc.Descendants().Where(x => x.Name.LocalName == "creator").Select(x => x.Value.Trim()).Where(x => x.Length > 0));
        var items = doc.Descendants().Where(x => x.Name.LocalName == "item").Where(x => x.Attribute("id") is not null)
            .GroupBy(x => (string)x.Attribute("id")!).ToDictionary(g => g.Key, g => g.First());
        var coverId = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "meta" && (string?)x.Attribute("name") == "cover")?.Attribute("content")?.Value;
        var cover = items.Values.FirstOrDefault(x => ((string?)x.Attribute("properties") ?? "").Split(' ').Contains("cover-image"))
            ?? (coverId is not null ? items.GetValueOrDefault(coverId) : null)
            ?? items.Values.FirstOrDefault(x => ((string?)x.Attribute("media-type") ?? "").StartsWith("image/") && ((string?)x.Attribute("href") ?? "").Contains("cover", StringComparison.OrdinalIgnoreCase));
        var coverPath = "";
        var coverEntry = zip.GetEntry(Resolve(package.FullName, (string?)cover?.Attribute("href") ?? "") ?? "");
        if (cover is not null && coverEntry is not null && coverEntry.Length is > 0 and < 8 * 1024 * 1024
            && new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp" }.Contains(Path.GetExtension(coverEntry.FullName).ToLowerInvariant()))
        {
            Directory.CreateDirectory(PersonalLibrary.Folder);
            coverPath = Path.Combine(PersonalLibrary.Folder, id + Path.GetExtension(coverEntry.FullName).ToLowerInvariant());
            if (!File.Exists(coverPath)) { using var input = coverEntry.Open(); using var output = File.Create(coverPath); input.CopyTo(output); }
        }
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var nav = items.Values.FirstOrDefault(x => ((string?)x.Attribute("properties") ?? "").Split(' ').Contains("nav"));
        var ncx = items.Values.FirstOrDefault(x => (string?)x.Attribute("media-type") == "application/x-dtbncx+xml");
        foreach (var index in new[] { nav, ncx }.Where(x => x is not null))
        {
            var indexPath = Resolve(package.FullName, (string?)index!.Attribute("href") ?? "");
            var entry = zip.GetEntry(indexPath ?? "");
            if (entry is null) continue;
            try
            {
                var toc = Xml(entry);
                foreach (var a in toc.Descendants().Where(x => x.Name.LocalName == "a"))
                    if (Resolve(entry.FullName, (string?)a.Attribute("href") ?? "") is { } resolved) names.TryAdd(resolved, a.Value.Trim());
                foreach (var point in toc.Descendants().Where(x => x.Name.LocalName == "navPoint"))
                {
                    var src = point.Elements().FirstOrDefault(x => x.Name.LocalName == "content")?.Attribute("src")?.Value;
                    var label = point.Elements().FirstOrDefault(x => x.Name.LocalName == "navLabel")?.Value.Trim();
                    if (src is not null && label is not null && Resolve(entry.FullName, src) is { } resolved) names.TryAdd(resolved, label);
                }
            }
            catch (XmlException) { }
        }
        var chapters = new List<PreviewChapter>();
        foreach (var reference in doc.Descendants().Where(x => x.Name.LocalName == "itemref").Take(1000))
        {
            var item = items.GetValueOrDefault((string?)reference.Attribute("idref") ?? "");
            if (item is null || !new[] { "application/xhtml+xml", "text/html" }.Contains((string?)item.Attribute("media-type"))) continue;
            var resolved = Resolve(package.FullName, (string?)item.Attribute("href") ?? "");
            if (resolved is null || zip.GetEntry(resolved) is null) continue;
            chapters.Add(new(names.GetValueOrDefault(resolved, $"第 {chapters.Count + 1} 节"), resolved));
        }
        return new(string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(path) : title, author.Length > 0 ? author : "未标注作者", coverPath, chapters);
    }
    public static string ReadChapter(string path, PreviewChapter chapter)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry(chapter.ArchivePath) ?? throw new InvalidDataException("正文文件不存在。");
        if (entry.Length > 4 * 1024 * 1024) return "本节内容较大，请在阅读器中查看。";
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true);
        var html = reader.ReadToEnd();
        html = Regex.Replace(html, @"<(script|style)\b[^>]*>.*?</\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        html = Regex.Replace(html, @"</(?:p|div|h[1-6]|li|section|blockquote)>|<br\s*/?>", "\n\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<[^>]+>", "");
        var text = System.Net.WebUtility.HtmlDecode(html);
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n\s*\n\s*\n+", "\n\n").Trim();
        return text.Length > 50000 ? text[..50000] + "\n\n本节预览到此，共显示前 5 万字。" : text;
    }
}
