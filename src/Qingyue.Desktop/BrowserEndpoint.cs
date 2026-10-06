using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace EpubKindleFix;

// The fallback controls only a dedicated browser profile created by Qingyue.
// It never attaches to the user's everyday browser or reads its cookies.
internal sealed class EdgeEndpoint : IDisposable
{
    private readonly ClientWebSocket socket = new();
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private readonly CancellationTokenSource lifetime = new();
    private int sequence;
    private int navigationInProgress;
    private Process? dedicatedProcess;
    private IntPtr dedicatedWindow;
    public bool IsLoading => Volatile.Read(ref navigationInProgress) != 0;
    public bool IsForeground => dedicatedWindow != IntPtr.Zero && GetForegroundWindow() == dedicatedWindow;

    public static async Task<EdgeEndpoint> OpenAsync(string url, CancellationToken cancellation)
    {
        var executable = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "Application", "msedge.exe")
        }.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("未找到 Microsoft Edge，请安装后再使用备用上传。");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KindleBookRepair", "AmazonEdge-v1");
        Directory.CreateDirectory(profile);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in new[] { "--user-data-dir=" + profile, "--remote-debugging-address=127.0.0.1", "--remote-debugging-port=" + port,
            "--no-first-run", "--no-default-browser-check", "--new-window", "about:blank" }) start.ArgumentList.Add(argument);
        var launched = Process.Start(start) ?? throw new InvalidOperationException("无法打开轻阅专用 Edge 窗口。");
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var json = await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", cancellation);
                using var targets = JsonDocument.Parse(json);
                foreach (var target in targets.RootElement.EnumerateArray())
                {
                    if (target.GetProperty("type").GetString() != "page" || target.GetProperty("url").GetString() != "about:blank") continue;
                    var address = new Uri(target.GetProperty("webSocketDebuggerUrl").GetString()!);
                    if (!address.IsLoopback || address.Port != port) throw new InvalidOperationException("浏览器连接地址不正确。");
                    var endpoint = new EdgeEndpoint { dedicatedProcess = launched };
                    try
                    {
                        await endpoint.socket.ConnectAsync(address, cancellation);
                        _ = endpoint.ReceiveAsync();
                        await endpoint.CallAsync("Page.enable", new { });
                        await endpoint.NavigateAsync(url);
                        await endpoint.CaptureWindowAsync(cancellation);
                        if (endpoint.dedicatedWindow == IntPtr.Zero) throw new InvalidOperationException("专用 Edge 窗口尚未就绪，请关闭该窗口后重试。");
                        return endpoint;
                    }
                    catch { endpoint.Dispose(); throw; }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { }
            await Task.Delay(400, cancellation);
        }
        launched.Dispose();
        throw new InvalidOperationException("无法连接轻阅的 Edge 窗口。请关闭之前的轻阅上传专用 Edge 窗口后重试。");
    }

    private async Task CaptureWindowAsync(CancellationToken cancellation)
    {
        for (var attempt = 0; attempt < 20 && dedicatedWindow == IntPtr.Zero; attempt++)
        {
            dedicatedProcess?.Refresh();
            dedicatedWindow = dedicatedProcess?.MainWindowHandle ?? IntPtr.Zero;
            if (dedicatedWindow == IntPtr.Zero) await Task.Delay(150, cancellation);
        }
    }

    // Show/hide only the verified HWND of the process launched with our dedicated
    // profile. Never enumerate or alter ordinary Edge windows.
    public bool SetVisible(bool visible)
    {
        if (dedicatedProcess is null || dedicatedWindow == IntPtr.Zero || !IsWindow(dedicatedWindow)) return false;
        GetWindowThreadProcessId(dedicatedWindow, out var ownerProcess);
        if (ownerProcess != dedicatedProcess.Id) return false;
        ShowWindow(dedicatedWindow, visible ? 5 : 0);
        return true;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public async Task<JsonElement> CallAsync(string method, object parameters)
    {
        var id = Interlocked.Increment(ref sequence);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id, method, @params = parameters }));
            await writer.WaitAsync(lifetime.Token);
            try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, lifetime.Token); }
            finally { writer.Release(); }
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), lifetime.Token);
        }
        finally { pending.TryRemove(id, out _); }
    }

    public async Task<string> EvaluateAsync(string expression)
    {
        var result = await CallAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true });
        if (result.TryGetProperty("exceptionDetails", out _)) throw new InvalidOperationException("网页暂时无法继续操作。");
        return result.TryGetProperty("result", out var value) && value.TryGetProperty("value", out var raw) ? raw.GetRawText() : "null";
    }

    public async Task NavigateAsync(string url)
    {
        Interlocked.Exchange(ref navigationInProgress, 1);
        await CallAsync("Page.navigate", new { url });
    }

    private async Task ReceiveAsync()
    {
        var buffer = new byte[16384];
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), lifetime.Token);
                    if (received.MessageType == WebSocketMessageType.Close) throw new IOException("Edge 上传窗口已关闭。");
                    message.Write(buffer, 0, received.Count);
                    if (message.Length > 4 * 1024 * 1024) throw new InvalidDataException("网页返回内容过大。");
                } while (!received.EndOfMessage);
                using var json = JsonDocument.Parse(message.ToArray());
                var root = json.RootElement;
                if (root.TryGetProperty("method", out var eventMethod))
                {
                    if (eventMethod.GetString() == "Page.frameNavigated" && root.TryGetProperty("params", out var data)
                        && data.TryGetProperty("frame", out var frame) && !frame.TryGetProperty("parentId", out _))
                        Interlocked.Exchange(ref navigationInProgress, 1);
                    if (eventMethod.GetString() == "Page.domContentEventFired") Interlocked.Exchange(ref navigationInProgress, 0);
                }
                if (!root.TryGetProperty("id", out var id) || !pending.TryRemove(id.GetInt32(), out var completion)) continue;
                if (root.TryGetProperty("error", out _)) completion.TrySetException(new InvalidOperationException("浏览器未能完成网页操作。"));
                else completion.TrySetResult(root.GetProperty("result").Clone());
            }
        }
        catch (Exception ex)
        {
            foreach (var completion in pending.Values) completion.TrySetException(ex);
        }
    }

    public void Dispose()
    {
        if (lifetime.IsCancellationRequested) return;
        lifetime.Cancel(); socket.Dispose();
        foreach (var completion in pending.Values) completion.TrySetCanceled();
        pending.Clear();
        // Restore a detached fallback window so it cannot be left inaccessible.
        SetVisible(true);
        dedicatedProcess?.Dispose(); dedicatedProcess = null;
    }

    public void CloseDedicatedBrowser()
    {
        try { CallAsync("Browser.close", new { }).Wait(TimeSpan.FromSeconds(1)); }
        catch { /* The dedicated window may already have been closed by the user. */ }
        Dispose();
    }
}

internal sealed class BrowserEndpoint(Func<string, Task<string>> evaluate, Func<string, object, Task<JsonElement>> call)
{
    public Task<string> EvaluateAsync(string script) => evaluate(script);
    public Task<JsonElement> CallAsync(string method, object parameters) => call(method, parameters);
    public async Task<Uri?> AddressAsync()
    {
        var json = await EvaluateAsync("location.href");
        return Uri.TryCreate(JsonSerializer.Deserialize<string>(json), UriKind.Absolute, out var result) ? result : null;
    }
}
