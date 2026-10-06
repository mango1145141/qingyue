using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace EpubKindleFix;

public sealed record SharedSettings(string SenderEmail, string KindleEmail, bool AutoSend, bool AutoRepair, bool RememberHistory);
public sealed record SyncCredential(string WorkspaceId, string AccessKey);
public sealed record SyncState(SharedSettings Settings, string Revision, int Devices);
public sealed record SyncJoin(string WorkspaceId, string AccessKey, SharedSettings Settings, string Revision, int Devices);
public sealed record PairState(string PairCode, long ExpiresAt);
public sealed class SyncFailure(string message, HttpStatusCode status) : Exception(message)
{ public HttpStatusCode Status { get; } = status; }

public static class SettingsSync
{
    public const string Website = "https://kindle-zftrvo.v2.appdeploy.ai/";
    // Same public API origin used by the deployed AppDeploy browser client.
    private static readonly HttpClient Client = new() { BaseAddress = new Uri("https://api-v2.appdeploy.ai/app/kindle-zftrvo/"), Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static SharedSettings Shared(AppSettings s) => new(s.SenderEmail, s.KindleEmail, s.AutoSend, s.AutoRepair, s.RememberHistory);
    public static bool Connected(AppSettings s) => Credential(s) is not null;
    public static SyncCredential? Credential(AppSettings s)
    {
        try { return JsonSerializer.Deserialize<SyncCredential>(Dpapi.Unprotect(Convert.FromBase64String(s.ProtectedSyncCredential)), Json); }
        catch { return null; }
    }
    private static void SetCredential(AppSettings s, SyncCredential? value) => s.ProtectedSyncCredential = value is null ? "" : Convert.ToBase64String(Dpapi.Protect(JsonSerializer.Serialize(value, Json)));
    private static AppSettings Apply(AppSettings s, SharedSettings shared)
    {
        var next = s.ForAddresses(shared.SenderEmail, shared.KindleEmail, shared.AutoSend);
        next.AutoRepair = shared.AutoRepair;
        next.RememberHistory = shared.RememberHistory;
        return next;
    }
    private static async Task<T> Post<T>(string route, object body)
    {
        using var response = await Client.PostAsJsonAsync("api/sync/" + route, body, Json);
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => "同步请求过多，自动同步已暂停，请稍后手动重试。",
                HttpStatusCode.Unauthorized => "此设备的配对已失效，请重新配对。",
                HttpStatusCode.BadRequest => "配对信息已使用、已过期或格式不正确，请重新生成。",
                _ => "同步暂时不可用，设置已保留在本机。"
            };
            throw new SyncFailure(message, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(Json) ?? throw new InvalidOperationException("同步返回的数据不完整。");
    }
    public static void RecordChanges(AppSettings before, AppSettings after)
    {
        if (!Connected(after)) return;
        var pending = Pending(after);
        var old = JsonSerializer.SerializeToElement(Shared(before), Json);
        var next = JsonSerializer.SerializeToElement(Shared(after), Json);
        foreach (var field in next.EnumerateObject())
            if (old.GetProperty(field.Name).GetRawText() != field.Value.GetRawText()) pending[field.Name] = field.Value.Clone();
        after.SyncPendingJson = JsonSerializer.Serialize(pending, Json);
    }
    private static Dictionary<string, JsonElement> Pending(AppSettings s)
    { try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(s.SyncPendingJson, Json) ?? new(); } catch { return new(); } }
    public static async Task<AppSettings> SynchronizeAsync(AppSettings s)
    {
        var device = Credential(s);
        if (device is null) return s;
        var state = await Post<SyncState>("state", device);
        var pending = Pending(s);
        if (pending.Count > 0)
        {
            try { state = await Post<SyncState>("save", new { device.WorkspaceId, device.AccessKey, state.Revision, patch = pending }); }
            catch (SyncFailure failure) when (failure.Status == HttpStatusCode.Conflict)
            {
                state = await Post<SyncState>("state", device);
                state = await Post<SyncState>("save", new { device.WorkspaceId, device.AccessKey, state.Revision, patch = pending });
            }
        }
        var next = Apply(s, state.Settings);
        next.SyncPendingJson = "{}";
        return next;
    }
    public static async Task<AppSettings> CreateAsync(AppSettings s)
    {
        if (Connected(s)) return s;
        var joined = await Post<SyncJoin>("create", new { settings = Shared(s) });
        var next = Apply(s, joined.Settings);
        SetCredential(next, new(joined.WorkspaceId, joined.AccessKey));
        next.SyncPendingJson = "{}";
        return next;
    }
    public static Task<PairState> PairAsync(AppSettings s) => Post<PairState>("pair", Credential(s) ?? throw new InvalidOperationException("请先开启设备同步。"));
    public static async Task<AppSettings> ClaimAsync(AppSettings s, string input)
    {
        var code = input.Trim();
        if (Uri.TryCreate(code, UriKind.Absolute, out var link))
        {
            if (link.Scheme != "https" || link.Host != new Uri(Website).Host || !link.Fragment.StartsWith("#pair=")) throw new InvalidOperationException("请粘贴轻阅生成的配对链接。");
            code = Uri.UnescapeDataString(link.Fragment[6..]);
        }
        var joined = await Post<SyncJoin>("claim", new { pairCode = code });
        var next = Apply(s, joined.Settings);
        SetCredential(next, new(joined.WorkspaceId, joined.AccessKey));
        next.SyncPendingJson = "{}";
        return next;
    }
    public static async Task<AppSettings> DisconnectAsync(AppSettings s)
    {
        var device = Credential(s);
        if (device is not null)
        {
            try { await Post<JsonElement>("disconnect", device); }
            catch (SyncFailure failure) when (failure.Status == HttpStatusCode.Unauthorized) { /* Already revoked remotely. */ }
        }
        var next = s.ForAddresses(s.SenderEmail, s.KindleEmail, s.AutoSend);
        SetCredential(next, null); next.SyncPendingJson = "{}";
        return next;
    }
}
