using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EpubKindleFix;

public sealed record EpubRepairReport(
    string OutputPath,
    int ImagesFound,
    int ManifestEntriesAdded,
    IReadOnlyList<string> WebpImages,
    IReadOnlyList<string> MissingImageFiles,
    bool MimetypeValid);

public static class EpubRepairer
{
    private static readonly Dictionary<string, string> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png",
        [".gif"] = "image/gif", [".svg"] = "image/svg+xml", [".webp"] = "image/webp",
        [".bmp"] = "image/bmp"
    };

    private static readonly Regex ManifestRegex = new(
        @"<(?<tag>(?:[A-Za-z_][\w.-]*:)?manifest)\b(?<attrs>[^>]*)>(?<body>.*?)</\k<tag>\s*>",
        RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ItemRegex = new(
        @"<(?:[A-Za-z_][\w.-]*:)?item\b[^>]*>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AttributeRegex = new(
        @"(?<name>[A-Za-z_:][\w:.-]*)\s*=\s*(?:""(?<double>[^""]*)""|'(?<single>[^']*)')",
        RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HtmlReferenceRegex = new(
        @"\b(?:src|href)\s*=\s*(?:""(?<double>[^""]+)""|'(?<single>[^']+)')",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex CssUrlRegex = new(
        @"url\(\s*(?:""(?<double>[^""]+)""|'(?<single>[^']+)'|(?<plain>[^)'\s]+))\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public static EpubRepairReport Repair(string inputPath)
    {
        if (!File.Exists(inputPath)) throw new FileNotFoundException("找不到这本书。", inputPath);
        if (!string.Equals(Path.GetExtension(inputPath), ".epub", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("目前只支持 EPUB 电子书。请拖入 .epub 文件。");

        var outputDirectory = Path.Combine(QingyueData.Root, "Kindle 修复");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = ChooseOutputPath(outputDirectory, Path.GetFileNameWithoutExtension(inputPath));
        var tempPath = outputPath + ".tmp";

        try
        {
            using var source = ZipFile.OpenRead(inputPath);
            var files = source.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
            var names = new HashSet<string>(files.Select(e => e.FullName), StringComparer.Ordinal);
            var container = source.GetEntry("META-INF/container.xml")
                ?? throw new InvalidDataException("这不是有效的 EPUB：缺少 META-INF/container.xml。");

            string opfPath;
            using (var stream = container.Open())
            {
                var containerDoc = XDocument.Load(stream);
                opfPath = containerDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")
                    ?.Attribute("full-path")?.Value
                    ?? throw new InvalidDataException("EPUB 的 container.xml 中没有 OPF 路径。");
            }

            var packageEntry = source.GetEntry(opfPath)
                ?? throw new InvalidDataException($"EPUB 声明的 OPF 文件不存在：{opfPath}");
            string opf;
            using (var reader = new StreamReader(packageEntry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                opf = reader.ReadToEnd();
            opf = Regex.Replace(opf, @"(?i)(\bencoding\s*=\s*[""'])[^""']+([""'])", "$1UTF-8$2");

            var manifestMatch = ManifestRegex.Match(opf);
            if (!manifestMatch.Success)
                throw new InvalidDataException("EPUB 的 OPF 文件缺少有效的 manifest 清单。");

            var packageDirectory = DirectoryPart(opfPath);
            var images = files.Where(e => MediaTypes.ContainsKey(Path.GetExtension(e.FullName))).ToList();
            var webp = images.Where(e => string.Equals(Path.GetExtension(e.FullName), ".webp", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.FullName).ToList();
            var declared = new HashSet<string>(StringComparer.Ordinal);
            var usedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match attribute in AttributeRegex.Matches(opf))
                if (attribute.Groups["name"].Value == "id") usedIds.Add(AttributeValue(attribute));
            foreach (Match itemMatch in ItemRegex.Matches(manifestMatch.Groups["body"].Value))
            {
                foreach (Match attribute in AttributeRegex.Matches(itemMatch.Value))
                {
                    var name = attribute.Groups["name"].Value;
                    var value = AttributeValue(attribute);
                    if (name == "href")
                    {
                        var resolved = ResolveArchivePath(packageDirectory, value);
                        if (resolved is not null) declared.Add(resolved);
                    }
                }
            }

            var additions = new List<string>();
            foreach (var image in images)
            {
                if (declared.Contains(image.FullName)) continue;
                var relativeHref = RelativeArchivePath(packageDirectory, image.FullName);
                var idStem = Path.GetFileNameWithoutExtension(image.FullName);
                var safeStem = Regex.Replace(idStem, @"[^A-Za-z0-9_.-]", "_");
                if (safeStem.Length == 0) safeStem = "image";
                var id = "img-" + safeStem;
                for (var suffix = 2; usedIds.Contains(id); suffix++) id = "img-" + safeStem + "-" + suffix;
                usedIds.Add(id);

                var extension = Path.GetExtension(image.FullName);
                var mediaType = MediaTypes[extension];
                var prefix = manifestMatch.Groups["tag"].Value.Contains(':')
                    ? manifestMatch.Groups["tag"].Value[..(manifestMatch.Groups["tag"].Value.IndexOf(':') + 1)]
                    : string.Empty;
                additions.Add($"<{prefix}item id=\"{XmlAttribute(id)}\" href=\"{XmlAttribute(EncodeUriPath(relativeHref))}\" media-type=\"{mediaType}\"/>");
            }

            var repairedOpf = opf;
            if (additions.Count > 0)
            {
                var closeTag = "</" + manifestMatch.Groups["tag"].Value + ">";
                var insertion = "\n    " + string.Join("\n    ", additions) + "\n";
                var closeIndex = manifestMatch.Index + manifestMatch.Length - closeTag.Length;
                repairedOpf = opf.Insert(closeIndex, insertion);
            }

            var missingImageFiles = FindMissingImageReferences(files, names);
            if (File.Exists(tempPath)) File.Delete(tempPath);
            using (var output = ZipFile.Open(tempPath, ZipArchiveMode.Create))
            {
                var mimeEntry = output.CreateEntry("mimetype", CompressionLevel.NoCompression);
                using (var writer = new StreamWriter(mimeEntry.Open(), new UTF8Encoding(false))) writer.Write("application/epub+zip");

                foreach (var entry in files)
                {
                    if (entry.FullName == "mimetype") continue;
                    var target = output.CreateEntry(entry.FullName,
                        entry.FullName == opfPath ? CompressionLevel.Optimal : CompressionLevel.Optimal);
                    if (entry.FullName.EndsWith('/')) continue;
                    using var sourceStream = entry.Open();
                    using var targetStream = target.Open();
                    if (entry.FullName == opfPath)
                    {
                        using var writer = new StreamWriter(targetStream, new UTF8Encoding(false), leaveOpen: true);
                        writer.Write(repairedOpf);
                    }
                    else sourceStream.CopyTo(targetStream);
                }
            }

            var mimetypeValid = ValidateOutput(tempPath, opfPath, images, packageDirectory);
            if (!mimetypeValid) throw new InvalidDataException("修复文件的 EPUB 打包校验未通过，原文件没有被修改。");
            File.Move(tempPath, outputPath);
            return new EpubRepairReport(outputPath, images.Count, additions.Count, webp, missingImageFiles, mimetypeValid);
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }

    private static bool ValidateOutput(string path, string opfPath, List<ZipArchiveEntry> originalImages, string packageDirectory)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count == 0 || zip.Entries[0].FullName != "mimetype") return false;
        var mime = zip.GetEntry("mimetype");
        if (mime is null || mime.Length != 20) return false;
        using (var reader = new StreamReader(mime.Open(), Encoding.ASCII))
            if (reader.ReadToEnd() != "application/epub+zip") return false;

        var opfEntry = zip.GetEntry(opfPath);
        if (opfEntry is null) return false;
        string opf;
        using (var reader = new StreamReader(opfEntry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true)) opf = reader.ReadToEnd();
        var manifest = ManifestRegex.Match(opf);
        if (!manifest.Success) return false;
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match item in ItemRegex.Matches(manifest.Groups["body"].Value))
        foreach (Match attribute in AttributeRegex.Matches(item.Value))
            if (attribute.Groups["name"].Value == "href")
            {
                var resolved = ResolveArchivePath(packageDirectory, AttributeValue(attribute));
                if (resolved is not null) declared.Add(resolved);
            }
        return originalImages.All(image => declared.Contains(image.FullName));
    }

    private static List<string> FindMissingImageReferences(IEnumerable<ZipArchiveEntry> files, HashSet<string> names)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var document in files.Where(e => IsReferenceDocument(e.FullName)))
        {
            string text;
            try
            {
                using var reader = new StreamReader(document.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                text = reader.ReadToEnd();
            }
            catch { continue; }
            var pattern = document.FullName.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ? CssUrlRegex : HtmlReferenceRegex;
            foreach (Match match in pattern.Matches(text))
            {
                var reference = ReferenceValue(match);
                var resolved = ResolveArchivePath(DirectoryPart(document.FullName), reference);
                if (resolved is not null && MediaTypes.ContainsKey(Path.GetExtension(resolved)) && !names.Contains(resolved))
                    missing.Add(reference);
            }
        }
        return missing.ToList();
    }

    private static bool IsReferenceDocument(string name)
    {
        var ext = Path.GetExtension(name);
        return ext.Equals(".xhtml", StringComparison.OrdinalIgnoreCase) || ext.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".htm", StringComparison.OrdinalIgnoreCase) || ext.Equals(".css", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveArchivePath(string baseDirectory, string rawHref)
    {
        var href = rawHref.Trim();
        if (href.Length == 0 || href.StartsWith('#') || href.StartsWith("//", StringComparison.Ordinal)
            || Regex.IsMatch(href, @"^[A-Za-z][A-Za-z0-9+.-]*:")) return null;
        var cut = href.IndexOfAny(['?', '#']);
        if (cut >= 0) href = href[..cut];
        try { href = Uri.UnescapeDataString(href); }
        catch { return null; }
        href = href.Replace('\\', '/');
        var segments = new List<string>();
        var combined = href.StartsWith('/') ? href.TrimStart('/') : (baseDirectory.Length == 0 ? href : baseDirectory + "/" + href);
        foreach (var segment in combined.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) return null;
                segments.RemoveAt(segments.Count - 1);
            }
            else segments.Add(segment);
        }
        return string.Join('/', segments);
    }

    private static string RelativeArchivePath(string baseDirectory, string target)
    {
        var from = baseDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var to = target.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var common = 0;
        while (common < from.Length && common < to.Length && from[common] == to[common]) common++;
        return string.Join('/', Enumerable.Repeat("..", from.Length - common).Concat(to.Skip(common)));
    }

    private static string DirectoryPart(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path[..index];
    }

    private static string EncodeUriPath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
    private static string XmlAttribute(string value) => SecurityElement.Escape(value) ?? string.Empty;

    private static string AttributeValue(Match attribute) => System.Net.WebUtility.HtmlDecode(attribute.Groups["double"].Success
        ? attribute.Groups["double"].Value : attribute.Groups["single"].Value);

    private static string ReferenceValue(Match match)
    {
        if (match.Groups["double"].Success) return match.Groups["double"].Value;
        if (match.Groups["single"].Success) return match.Groups["single"].Value;
        return match.Groups["plain"].Value;
    }

    private static string ChooseOutputPath(string directory, string title)
    {
        var stem = title + "（已修复）";
        var candidate = Path.Combine(directory, stem + ".epub");
        for (var suffix = 2; File.Exists(candidate); suffix++) candidate = Path.Combine(directory, $"{stem} {suffix}.epub");
        return candidate;
    }
}
