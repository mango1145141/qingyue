using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;

namespace EpubKindleFix;

public static class BookCoverStore
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim Downloads = new(2);
    private static readonly ConcurrentDictionary<string, Lazy<Task<BitmapSource?>>> Pending = new();
    private static readonly Dictionary<string, BitmapSource> Memory = new();
    private static readonly Queue<string> MemoryOrder = new();
    private static readonly object MemoryLock = new();
    private static readonly Lazy<Dictionary<string, long>> CoverIds = new(ReadCoverIds);
    private static readonly string CacheFolder = Path.Combine("G:\\chatgpt", "轻阅数据", "封面");
    private static Uri ResourceUri(string path) => new($"/{typeof(BookCoverStore).Assembly.GetName().Name};component/{path}", UriKind.Relative);

    private static Dictionary<string, long> ReadCoverIds()
    {
        try
        {
            var resource = Application.GetResourceStream(ResourceUri("Assets/BookCovers.json"));
            using var stream = resource?.Stream;
            return stream is null ? new() : JsonSerializer.Deserialize<Dictionary<string, long>>(stream) ?? new();
        }
        catch { return new(); }
    }

    public static async Task<BitmapSource?> LoadAsync(DiscoveryBook book)
    {
        lock (MemoryLock) if (Memory.TryGetValue(book.Id, out var image)) return image;
        var loading = Pending.GetOrAdd(book.Id, _ => new Lazy<Task<BitmapSource?>>(() => LoadCoreAsync(book)));
        try
        {
            var image = await loading.Value.ConfigureAwait(false);
            if (image is not null)
                lock (MemoryLock)
                {
                    if (!Memory.ContainsKey(book.Id))
                    {
                        Memory[book.Id] = image;
                        MemoryOrder.Enqueue(book.Id);
                        while (MemoryOrder.Count > 48) Memory.Remove(MemoryOrder.Dequeue());
                    }
                }
            return image;
        }
        finally
        {
            ((ICollection<KeyValuePair<string, Lazy<Task<BitmapSource?>>>>)Pending).Remove(new(book.Id, loading));
        }
    }

    private static async Task<BitmapSource?> LoadCoreAsync(DiscoveryBook book)
    {
        // A few bundled covers also make the first bookshelf useful offline.
        try
        {
            var resource = Application.GetResourceStream(ResourceUri($"Assets/Covers/{book.Id}.jpg"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var bundled = await DecodeAsync(buffer.ToArray()).ConfigureAwait(false);
                if (bundled is not null) return bundled;
            }
        }
        catch { /* Covers for the remaining titles are loaded when displayed. */ }
        var id = book.CoverId > 0 ? book.CoverId : CoverIds.Value.GetValueOrDefault(book.Id);
        if (id <= 0) return null;
        var cachePath = Path.Combine(CacheFolder, id + ".jpg");
        try
        {
            if (File.Exists(cachePath))
            {
                var cached = await DecodeAsync(await File.ReadAllBytesAsync(cachePath).ConfigureAwait(false)).ConfigureAwait(false);
                if (cached is not null) return cached;
            }
        }
        catch { /* A failed cache read must not block the bookshelf. */ }
        await Downloads.WaitAsync().ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://covers.openlibrary.org/b/id/{id}-L.jpg?default=false");
            request.Headers.UserAgent.ParseAdd("QingyueDesktop/1.9.0");
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 8 * 1024 * 1024) return null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int count;
            while ((count = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + count > 8 * 1024 * 1024) return null;
                buffer.Write(chunk, 0, count);
            }
            var bytes = buffer.ToArray();
            var image = await DecodeAsync(bytes).ConfigureAwait(false);
            if (image is null) return null;
            try
            {
                Directory.CreateDirectory(CacheFolder);
                var temporary = cachePath + ".tmp";
                await File.WriteAllBytesAsync(temporary, bytes).ConfigureAwait(false);
                File.Move(temporary, cachePath, overwrite: true);
            }
            catch { /* A read-only cache still permits viewing the downloaded cover. */ }
            return image;
        }
        catch { return null; }
        finally { Downloads.Release(); }
    }

    private static Task<BitmapSource?> DecodeAsync(byte[] bytes) => Task.Run<BitmapSource?>(() =>
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 600;
            image.StreamSource = stream;
            image.EndInit();
            if (image.PixelWidth < 50 || image.PixelHeight < 70) return null;
            image.Freeze();
            return image;
        }
        catch { return null; }
    });
}
