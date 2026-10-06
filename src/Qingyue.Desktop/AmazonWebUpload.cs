using System.IO;
using System.Text.Json;

namespace EpubKindleFix;

public enum WebUploadOutcome { Submitted, Deferred, Uncertain, Rejected }
public sealed record WebUploadResult(WebUploadOutcome Outcome, string Message);
public enum WebUploadInteraction { None, Login, Manual }
public sealed record WebUploadUpdate(string Message, double? Progress, WebUploadInteraction Interaction, bool SubmissionStarted);

internal sealed class AmazonWebUpload
{
    public const string Website = "https://www.amazon.com/sendtokindle";
    public const long MailLimit = 50_000_000;
    public const long WebLimit = 200L * 1024 * 1024;
    private readonly string path;
    private readonly string name;
    private readonly long length;
    private readonly string token = Guid.NewGuid().ToString("N");
    private string documentToken = "";
    private bool injected;
    private bool automationStopped;
    private bool submitting;
    private DateTimeOffset injectedAt;
    private DateTimeOffset? startedAt;
    private DateTimeOffset pageWaitStarted = DateTimeOffset.Now;
    public bool SubmissionStarted { get; private set; }
    public bool Ready { get; private set; }
    public string Status { get; private set; } = "正在后台连接亚马逊，进度会显示在轻阅主页。";
    public double? Progress { get; private set; }
    public WebUploadResult? Result { get; private set; }
    public WebUploadInteraction Interaction { get; private set; }

    public AmazonWebUpload(string filePath)
    {
        path = Path.GetFullPath(filePath);
        if (!File.Exists(path)) throw new FileNotFoundException("修复版已移动或删除，请重新检查原书。");
        if (!Path.GetExtension(path).Equals(".epub", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("目前网页上传只接入 EPUB。");
        length = new FileInfo(path).Length;
        if (length > WebLimit) throw new InvalidDataException("这本书超过亚马逊网页的 200 MB 限制，修复版已保留。");
        name = Path.GetFileName(path);
    }

    public static bool IsUploadPage(Uri? uri) => uri is { Scheme: "https", Host: "www.amazon.com" }
        && uri.AbsolutePath.TrimEnd('/').Equals("/sendtokindle", StringComparison.OrdinalIgnoreCase);

    private string Script(string body) => "(() => { if (location.origin !== 'https://www.amazon.com' || location.pathname.replace(/\\/$/,'') !== '/sendtokindle') return null; "
        + "const visible = e => !!e && e.getClientRects().length > 0 && getComputedStyle(e).visibility !== 'hidden'; "
        + body.Replace("__TOKEN__", JsonSerializer.Serialize(token)).Replace("__NAME__", JsonSerializer.Serialize(name)).Replace("__SIZE__", length.ToString(System.Globalization.CultureInfo.InvariantCulture)) + " })()";

    public async Task TickAsync(BrowserEndpoint endpoint)
    {
        if (Result is not null) return;
        if (!automationStopped) Interaction = WebUploadInteraction.None;
        var address = await endpoint.AddressAsync();
        if (!IsUploadPage(address))
        {
            Ready = false;
            Status = SubmissionStarted ? "网页已离开上传页，请先确认亚马逊发送结果。" : "请完成亚马逊登录或验证码，再返回 Send to Kindle 页面。";
            Interaction = WebUploadInteraction.Login;
            if (SubmissionStarted) Result = new(WebUploadOutcome.Uncertain, Status);
            return;
        }
        using var state = JsonDocument.Parse(await endpoint.EvaluateAsync(Script("""
            window.__qingyueDocumentToken ||= crypto.randomUUID();
            const signIn = document.querySelector('#s2k-dnd-sign-in-button');
            const add = [...document.querySelectorAll('.s2k-dnd-add-your-files-button')].find(visible);
            const readyRows = [...document.querySelectorAll('#ready-2-send .s2k-r2s-file-item')].filter(visible);
            const send = document.querySelector('#s2k-r2s-send-button');
            const watch = window.__qingyueUpload;
            const current = watch?.token === __TOKEN__;
            const row = [...document.querySelectorAll('#s2k-dnd-sip-file-list .s2k-dnd-file-item')]
                .find(e => { const t = e.querySelector('.s2k-dnd-file-title'); return t && (t.title === __NAME__ || t.textContent.trim() === __NAME__); });
            const done = !!row?.querySelector('.s2k-dnd-icons-done');
            const failed = !!row?.querySelector('.s2k-dnd-icons-failed');
            const progress = Number(document.querySelector('#s2k-sip-progress-bar')?.getAttribute('data-progress-percentage'));
            const challenge = [...document.querySelectorAll('#captchacharacters,input[name="cvf_captcha_input"],#auth-mfa-otpcode,#cvf-input-code')].some(visible);
            return {document:window.__qingyueDocumentToken, login:visible(signIn), ready:!!add,
                rows:readyRows.length, send:visible(send) && !send.disabled, current, selected:current && watch.selected,
                attempted:current && watch.attempted, accepted:current && watch.accepted,
                rejected:current && watch.rejected, done, failed, challenge, progress:Number.isFinite(progress)?progress:0};
            """)));
        if (state.RootElement.ValueKind != JsonValueKind.Object) return;
        var s = state.RootElement;
        var page = s.GetProperty("document").GetString()!;
        Ready = s.GetProperty("ready").GetBoolean() || s.GetProperty("rows").GetInt32() > 0;
        if (documentToken.Length > 0 && page != documentToken)
        {
            if (SubmissionStarted) { Result = new(WebUploadOutcome.Uncertain, "上传期间网页重新加载，请先确认亚马逊是否收到，避免重复发送。"); return; }
            injected = false; automationStopped = false;
            Interaction = WebUploadInteraction.None; pageWaitStarted = DateTimeOffset.Now;
        }
        documentToken = page;
        if (s.GetProperty("attempted").GetBoolean()) { SubmissionStarted = true; startedAt ??= DateTimeOffset.Now; }
        if (SubmissionStarted && (s.GetProperty("accepted").GetBoolean() || s.GetProperty("done").GetBoolean()))
        {
            Progress = 100;
            Result = new(WebUploadOutcome.Submitted, "亚马逊已接收，等待转换与 Kindle 联网同步。"); return;
        }
        if (s.GetProperty("rejected").GetBoolean())
        { Result = new(WebUploadOutcome.Rejected, "亚马逊没有接受这次发送，请查看官方页面的错误提示后重试。"); return; }
        if (SubmissionStarted && s.GetProperty("failed").GetBoolean())
        { Result = new(WebUploadOutcome.Uncertain, "网页发送失败或连接中断，请先确认亚马逊记录后再决定是否重试。"); return; }
        if (SubmissionStarted)
        {
            Progress = Math.Clamp(s.GetProperty("progress").GetDouble(), 0, 100);
            Status = Progress >= 100 ? "文件已上传，正在等待亚马逊接收确认…" : $"正在上传到亚马逊 · {Progress:F0}%";
            if (DateTimeOffset.Now - startedAt > TimeSpan.FromMinutes(30)) Result = new(WebUploadOutcome.Uncertain, "等待时间较长，请在亚马逊页面确认发送结果后继续。");
            return;
        }
        if (s.GetProperty("login").GetBoolean() || s.GetProperty("challenge").GetBoolean())
        { Ready = false; Interaction = WebUploadInteraction.Login; Status = "请完成亚马逊登录或验证，完成后会回到主页继续上传。"; return; }
        if (automationStopped) return;
        if (!injected)
        {
            if (s.GetProperty("rows").GetInt32() > 0)
            { PauseAutomation("官方页面已有其他待发送文件，请先处理或移除，再点击“继续自动上传”。"); return; }
            if (!s.GetProperty("ready").GetBoolean())
            {
                Status = "正在等待亚马逊上传页面准备完成…";
                if (DateTimeOffset.Now - pageWaitStarted > TimeSpan.FromSeconds(45))
                    PauseAutomation("上传页面尚未就绪，请查看官方页面的网络、验证或设备提示后继续。");
                return;
            }
            await InstallObserverAsync(endpoint);
            var prepared = JsonSerializer.Deserialize<string>(await endpoint.EvaluateAsync(Script("""
                const button = [...document.querySelectorAll('.s2k-dnd-add-your-files-button')].find(visible);
                if (!button) return 'missing';
                let captured = null;
                const click = HTMLInputElement.prototype.click;
                HTMLInputElement.prototype.click = function() {
                    if (this.type === 'file') {
                        captured = this; this.id = 'qingyue-stk-file-input'; this.style.display = 'none';
                        if (!this.isConnected) document.body.appendChild(this);
                        const changed = this.onchange;
                        this.onchange = function(event) {
                            const watch = window.__qingyueUpload;
                            if (watch?.token === __TOKEN__) watch.selected = this.files?.length === 1 && this.files[0].name === __NAME__ && this.files[0].size === __SIZE__;
                            return changed?.call(this,event);
                        };
                        return;
                    }
                    return click.call(this);
                };
                try { button.click(); } finally { HTMLInputElement.prototype.click = click; }
                return captured ? 'ready' : 'missing';
                """)));
            if (prepared != "ready") { PauseAutomation("上传页面暂不支持自动选入文件，可以在网页选择修复版，或使用 Edge 继续。"); return; }
            if (!IsUploadPage(await endpoint.AddressAsync())) return;
            var document = await endpoint.CallAsync("DOM.getDocument", new { depth = 0 });
            var input = await endpoint.CallAsync("DOM.querySelector", new { nodeId = document.GetProperty("root").GetProperty("nodeId").GetInt32(), selector = "#qingyue-stk-file-input" });
            var nodeId = input.GetProperty("nodeId").GetInt32();
            if (nodeId == 0) throw new InvalidOperationException("亚马逊的上传控件已变化，请使用网页手动选择文件。");
            if (!IsUploadPage(await endpoint.AddressAsync())) return;
            await endpoint.CallAsync("DOM.setFileInputFiles", new { nodeId, files = new[] { path } });
            injected = true; injectedAt = DateTimeOffset.Now;
            Status = "修复版已交给官方页面，正在准备发送…";
            return;
        }
        if (!s.GetProperty("selected").GetBoolean() || s.GetProperty("rows").GetInt32() != 1 || !s.GetProperty("send").GetBoolean())
        {
            if (DateTimeOffset.Now - injectedAt > TimeSpan.FromSeconds(25))
            { PauseAutomation("请查看官方页面的文件或设备提示，处理后点击“继续自动上传”。"); }
            return;
        }
        submitting = true;
        string clicked;
        try { clicked = await endpoint.EvaluateAsync(Script("""
            const watch = window.__qingyueUpload;
            if (!watch || watch.token !== __TOKEN__ || watch.attempted || !watch.selected) return false;
            const rows = [...document.querySelectorAll('#ready-2-send .s2k-r2s-file-item')].filter(visible);
            if (rows.length !== 1) return false;
            const library = document.querySelector('#s2k-r2s-add2lib');
            if (library instanceof HTMLInputElement && !library.checked) library.click();
            const send = document.querySelector('#s2k-r2s-send-button');
            if (!visible(send) || send.disabled) return false;
            watch.attempted = true; send.click(); return true;
            """)); }
        catch { SubmissionStarted = true; startedAt ??= DateTimeOffset.Now; throw; }
        finally { submitting = false; }
        if (clicked == "true") { SubmissionStarted = true; startedAt = DateTimeOffset.Now; Status = "正在上传到亚马逊…"; }
    }

    private async Task InstallObserverAsync(BrowserEndpoint endpoint)
    {
        await endpoint.EvaluateAsync(Script("""
            window.__qingyueUpload?.cleanup?.();
            const state = {token:__TOKEN__, selected:false, attempted:false, accepted:false, rejected:false};
            const open = XMLHttpRequest.prototype.open, send = XMLHttpRequest.prototype.send;
            const matches = new WeakSet();
            const opened = new WeakSet();
            function hookedOpen(method, url, ...args) {
                try { const u = new URL(url, location.href); if (method.toUpperCase() === 'POST' && u.origin === location.origin && u.pathname === '/sendtokindle/send-v2') opened.add(this); } catch {}
                return open.call(this, method, url, ...args);
            }
            function hookedSend(body) {
                if (opened.has(this)) {
                    try {
                        const data = JSON.parse(body);
                        // Only match the selected file. No tokens, credentials, or response bodies are read or exported.
                        if (data.inputFileName === __NAME__ && data.fileSize === __SIZE__) matches.add(this);
                    } catch {}
                    if (matches.has(this)) {
                        state.attempted = true;
                        this.addEventListener('load', () => {
                            const json = (this.getResponseHeader('content-type') || '').includes('json');
                            if (this.status >= 200 && this.status < 300 && json) state.accepted = true;
                            if (this.status >= 400) state.rejected = true;
                        }, {once:true});
                    }
                }
                return send.call(this, body);
            }
            XMLHttpRequest.prototype.open = hookedOpen;
            XMLHttpRequest.prototype.send = hookedSend;
            state.cleanup = () => {
                if (XMLHttpRequest.prototype.open === hookedOpen) XMLHttpRequest.prototype.open = open;
                if (XMLHttpRequest.prototype.send === hookedSend) XMLHttpRequest.prototype.send = send;
            };
            window.__qingyueUpload = state; return true;
            """));
    }

    public void ResumeAutomation() { automationStopped = false; Interaction = WebUploadInteraction.None; pageWaitStarted = injectedAt = DateTimeOffset.Now; }
    public void PauseAutomation(string message) { automationStopped = true; Interaction = WebUploadInteraction.Manual; Status = message; }
    public WebUploadResult Interrupt() => new(SubmissionStarted || submitting ? WebUploadOutcome.Uncertain : WebUploadOutcome.Deferred,
        SubmissionStarted || submitting ? "网页发送结果待确认，请先查看亚马逊记录，再决定是否重新发送。" : "修复版已保留，可以稍后从发送队列继续网页上传。");
}
