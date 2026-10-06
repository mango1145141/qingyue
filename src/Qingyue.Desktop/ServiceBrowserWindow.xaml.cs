using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace EpubKindleFix;

public enum BrowserService { Amazon, Comic }

public partial class ServiceBrowserWindow : Window
{
    private readonly BrowserService service;
    private readonly System.Windows.Threading.DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<CoreWebView2DownloadOperation, string> downloads = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim webActionGate = new(1, 1);
    private Task? initializing;
    private BrowserEndpoint? endpoint;
    private BrowserEndpoint? embeddedEndpoint;
    private EdgeEndpoint? edge;
    private AmazonWebUpload? upload;
    private TaskCompletionSource<WebUploadResult>? completion;
    private bool polling;
    private bool shutDown;
    private bool switchingBrowser;
    private bool loginReturnRequested;
    private bool? lastReady;
    private string? pendingComicQuery;
    private string? lastComicQuery;
    private int searchRecoveries;
    private bool needsComicSearchAfterLogin;
    private bool comicSearchFlow;
    private bool embeddedLoading = true;
    private bool backgroundUpload;
    private bool returnAfterAmazonLogin;
    private WebUploadInteraction presentedInteraction;
    private DateTimeOffset navigationStarted;
    public bool HasActiveWork => completion is not null || downloads.Count > 0;
    public event Action<bool>? LoginReady;
    public event Action<bool>? WebForAllChanged;
    public event Action<string>? DownloadFinished;
    public event Action<string>? StatusChanged;
    public event Action<string>? SearchSubmitted;
    public event Action<WebUploadUpdate>? UploadUpdated;
    private CoreWebView2? Core => service == BrowserService.Amazon ? AmazonBrowser.Core : Browser.CoreWebView2;
    private string Home => service == BrowserService.Amazon ? AmazonWebUpload.Website : "https://koz.moe/";

    public ServiceBrowserWindow(BrowserService service, bool webForAll = false)
    {
        this.service = service;
        InitializeComponent();
        ShowBrowser();
        Title = service == BrowserService.Amazon ? "轻阅 · 亚马逊上传" : "轻阅 · koz.moe 漫画";
        Heading.Text = service == BrowserService.Amazon ? "好书，原样抵达。" : "下一部，想看什么？";
        Subtitle.Text = service == BrowserService.Amazon ? "SEND TO KINDLE  /  官方网页 · 单本最高 200 MB" : "KOZ.MOE  /  登录 · 搜索 · 漫画推送";
        BrowserStatus.Text = service == BrowserService.Amazon ? "请在亚马逊官方页面登录。网页上传无需发件邮箱授权。"
            : "在 koz.moe 选择漫画和卷数，使用站内“推到 Kindle”。首次推送请在网站验证 Kindle 地址。";
        AddressLabel.Text = Home;
        WebForAllCheck.Visibility = service == BrowserService.Amazon ? Visibility.Visible : Visibility.Collapsed;
        WebForAllCheck.IsChecked = webForAll;
        ExternalButton.Content = service == BrowserService.Amazon ? "使用 Edge 继续 ↗" : "在浏览器中打开 ↗";
        poll.Tick += async (_, _) => await PollAsync();
        Closed += (_, _) => { shutDown = true; poll.Stop(); lifetime.Cancel(); edge?.Dispose(); AmazonBrowser.Dispose(); Browser.Dispose(); };
        Closing += OnClosing;
    }

    private Task InitializeAsync() => initializing ??= InitializeCoreAsync();
    private async Task InitializeCoreAsync()
    {
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KindleBookRepair",
            service == BrowserService.Amazon ? "AmazonBrowser-v1" : "ComicBrowser-v1");
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
        if (service == BrowserService.Amazon) await AmazonBrowser.InitializeAsync(environment);
        else await Browser.EnsureCoreWebView2Async(environment);
        var core = Core!;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        embeddedEndpoint = new(core.ExecuteScriptAsync, async (method, parameters) =>
        {
            var json = await core.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(parameters));
            using var value = JsonDocument.Parse(json); return value.RootElement.Clone();
        });
        endpoint ??= embeddedEndpoint;
        core.NavigationStarting += (_, args) =>
        {
            if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var address) || address.Scheme != "https")
            { args.Cancel = true; BrowserStatus.Text = "该链接不是 HTTPS 网页，请选择网站内的安全入口。"; return; }
            AddressLabel.Text = address.GetLeftPart(UriPartial.Path);
            embeddedLoading = true;
            navigationStarted = DateTimeOffset.Now;
            if (service == BrowserService.Comic && address.Host == "koz.moe" && address.AbsolutePath == "/login.php")
            {
                // Returning home is armed only by a login flow; normal searches stay open.
                loginReturnRequested = !comicSearchFlow;
                if (comicSearchFlow && lastComicQuery is not null) { needsComicSearchAfterLogin = true; pendingComicQuery = lastComicQuery; }
            }
        };
        core.NavigationCompleted += async (_, args) =>
        {
            if (shutDown || edge is not null) return;
            embeddedLoading = !args.IsSuccess;
            if (!args.IsSuccess)
            {
                ShowError("网页连接暂时失败，请检查网络后重新打开，或使用浏览器继续。");
                if (completion is not null && upload is not null)
                {
                    if (upload.SubmissionStarted) Complete(upload.Interrupt());
                    else { upload.PauseAutomation(ErrorText.Text); PresentInteraction(WebUploadInteraction.Manual, upload.Status); }
                }
                return;
            }
            ErrorCard.Visibility = Visibility.Collapsed;
            ShowBrowser();
            BackButton.IsEnabled = core.CanGoBack;
            try
            {
                if (service == BrowserService.Comic) await SubmitComicSearchAsync();
                await PollAsync();
            }
            catch { BrowserStatus.Text = "网页暂时无法继续，请返回首页或重新打开网页。"; }
        };
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var address) && address.Scheme == "https") core.Navigate(address.AbsoluteUri);
        };
        core.DownloadStarting += DownloadStarting;
        core.ProcessFailed += (_, _) =>
        {
            ShowError("网页组件暂时停止，请使用 Edge 继续，或关闭软件后重新打开。");
            if (upload?.SubmissionStarted == true) Complete(upload.Interrupt());
            else if (completion is not null && upload is not null)
            { upload.PauseAutomation(ErrorText.Text); PresentInteraction(WebUploadInteraction.Manual, upload.Status); }
        };
    }

    public async Task OpenAsync(bool login = false)
    {
        Show(); Activate();
        if (service == BrowserService.Amazon)
        {
            // Explicit inspection stays open. A first login returns automatically.
            if (completion is null) returnAfterAmazonLogin = lastReady != true;
            edge?.SetVisible(true);
        }
        if (service == BrowserService.Comic)
        {
            comicSearchFlow = false;
            loginReturnRequested = login;
            if (login) { lastComicQuery = pendingComicQuery = null; needsComicSearchAfterLogin = false; }
        }
        try
        {
            if (edge is not null && service == BrowserService.Amazon)
            {
                if (completion is not null) { BrowserStatus.Text = "正在专用 Edge 窗口中上传，请在那里继续登录或查看结果。"; return; }
                await HomeNavigateAsync(); poll.Start(); return;
            }
            await InitializeAsync();
            if (completion is null)
            {
                ErrorCard.Visibility = Visibility.Collapsed; ShowBrowser();
                Core!.Navigate(service == BrowserService.Comic && login ? "https://koz.moe/login.php" : Home);
            }
            poll.Start();
        }
        catch (Exception ex)
        {
            initializing = null;
            ShowError(ex is WebView2RuntimeNotFoundException ? "电脑缺少微软网页组件。亚马逊上传可使用 Edge 继续；漫画可使用浏览器打开。" : "网页暂时无法打开，请检查网络后重试，或使用浏览器继续。");
        }
    }

    public async Task SearchComicAsync(string query)
    {
        if (service != BrowserService.Comic) return;
        comicSearchFlow = true;
        loginReturnRequested = false;
        Show(); Activate();
        lastComicQuery = query.Trim(); pendingComicQuery = lastComicQuery; searchRecoveries = 0; needsComicSearchAfterLogin = false;
        await InitializeAsync();
        ErrorCard.Visibility = Visibility.Collapsed; Browser.Visibility = Visibility.Visible;
        BrowserStatus.Text = "正在搜索漫画：" + lastComicQuery;
        Browser.CoreWebView2.Navigate("https://koz.moe/"); poll.Start();
    }

    private async Task SubmitComicSearchAsync()
    {
        if (!comicSearchFlow || pendingComicQuery is null || endpoint is null || loginReturnRequested) return;
        var query = pendingComicQuery;
        var script = """
            (() => {
                if (location.origin !== 'https://koz.moe') return 'foreign';
                if (document.querySelector('form[name="login"] input[type="password"]')) return 'login';
                const form = document.querySelector('form[name="search2"]');
                const input = form?.querySelector('input[name="s"]');
                if (!form || !input) return 'missing';
                const action = new URL(form.action, location.href);
                if (action.origin !== location.origin || action.pathname !== '/list.php' || form.method.toLowerCase() !== 'get') return 'changed';
                input.value = __QUERY__; input.dispatchEvent(new Event('input',{bubbles:true}));
                form.target = '_self';
                setTimeout(() => HTMLFormElement.prototype.submit.call(form),0);
                return 'submitted';
            })()
            """;
        pendingComicQuery = null;
        var status = JsonSerializer.Deserialize<string>(await endpoint.EvaluateAsync(script.Replace("__QUERY__", JsonSerializer.Serialize(query))));
        if (status == "submitted")
        {
            needsComicSearchAfterLogin = false;
            BrowserStatus.Text = "已提交漫画搜索，请在结果中选择漫画，再选择要推送的卷数。";
            SearchSubmitted?.Invoke(query);
        }
        else if (status == "login") { pendingComicQuery = query; needsComicSearchAfterLogin = true; BrowserStatus.Text = "请先完成 koz.moe 登录，登录后继续搜索原来的漫画名。"; }
        else BrowserStatus.Text = "网站搜索表单暂不可用，可以点击“首页”重试，或在浏览器中继续搜索。";
    }

    public async Task<WebUploadResult> UploadAsync(string path)
    {
        if (completion is not null) throw new InvalidOperationException("还有一本书正在网页上传，请先完成当前任务。");
        upload = new AmazonWebUpload(path);
        var done = new TaskCompletionSource<WebUploadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        completion = done;
        UploadCard.Visibility = Visibility.Visible;
        FileLabel.Text = Path.GetFileName(path) + $" · {new FileInfo(path).Length / 1024.0 / 1024.0:F1} MB";
        UploadNotice.Text = "正在后台连接亚马逊，进度会显示在轻阅主页。";
        UploadProgress.Visibility = Visibility.Visible;
        UploadProgress.IsIndeterminate = true;
        ContinueButton.IsEnabled = LaterButton.IsEnabled = true;
        backgroundUpload = true; presentedInteraction = WebUploadInteraction.None; returnAfterAmazonLogin = false;
        PublishUpload();
        try
        {
            try
            {
                HideForBackground();
                if (edge is not null) await HomeNavigateAsync();
                else
                {
                    await InitializeAsync().WaitAsync(TimeSpan.FromSeconds(30), lifetime.Token);
                    ErrorCard.Visibility = Visibility.Collapsed; ShowBrowser();
                    await HomeNavigateAsync();
                }
            }
            catch (Exception ex) when (!shutDown)
            {
                if (initializing?.IsFaulted == true) initializing = null;
                var message = ex is WebView2RuntimeNotFoundException ? "缺少微软网页组件，可使用 Edge 继续上传。"
                    : "后台连接暂未完成，请检查网络后重新打开网页，或使用 Edge 继续。";
                ShowError(message); upload.PauseAutomation(message);
                PresentInteraction(WebUploadInteraction.Manual, message);
            }
            poll.Start();
            return await done.Task;
        }
        finally { completion = null; backgroundUpload = false; returnAfterAmazonLogin = false; }
    }

    private async Task PollAsync()
    {
        if (polling || switchingBrowser || shutDown || endpoint is null || completion?.Task.IsCompleted == true) return;
        if (edge is null ? embeddedLoading : edge.IsLoading)
        {
            if (completion is not null && upload is not null && DateTimeOffset.Now - navigationStarted > TimeSpan.FromSeconds(45))
            {
                if (upload.SubmissionStarted) Complete(upload.Interrupt());
                else
                {
                    upload.PauseAutomation("连接等待较长，请检查官方页面的网络或验证提示，也可以稍后发送。");
                    PresentInteraction(WebUploadInteraction.Manual, upload.Status);
                }
            }
            return;
        }
        if (!await webActionGate.WaitAsync(0)) return;
        polling = true;
        try
        {
            if (service == BrowserService.Comic)
            {
                var state = await endpoint.EvaluateAsync("""
                    (() => {
                        if (location.origin !== 'https://koz.moe') return null;
                        const visible = e => !!e && e.getClientRects().length > 0;
                        if ([...document.querySelectorAll('a[href]')].some(a => /\/(?:logout|loginout)(?:\.php)?(?:[?#]|$)/i.test(a.getAttribute('href') || '') && visible(a))) return true;
                        const nav = document.querySelector('.nav_user2,.nav_user');
                        if (!nav) return null;
                        const loggedOut = [...nav.querySelectorAll('a[href]')].some(a => new URL(a.href,location.href).pathname === '/login.php');
                        return !loggedOut;
                    })()
                    """);
                if (state is "true" or "false")
                {
                    var ready = state == "true";
                    ReportReady(ready);
                    if (ready && loginReturnRequested)
                    {
                        loginReturnRequested = false;
                        comicSearchFlow = false;
                        pendingComicQuery = lastComicQuery = null;
                        needsComicSearchAfterLogin = false;
                        Hide();
                        Owner?.Show(); Owner?.Activate();
                        return;
                    }
                    if (ready && needsComicSearchAfterLogin && lastComicQuery is not null && searchRecoveries == 0 && Browser.CoreWebView2 is { } core
                        && Uri.TryCreate(core.Source, UriKind.Absolute, out var current) && current.AbsolutePath is "/" or "/index.php")
                    { searchRecoveries++; pendingComicQuery = lastComicQuery; await SubmitComicSearchAsync(); }
                }
            }
            else if (upload is not null && completion is not null)
            {
                await upload.TickAsync(endpoint);
                if (upload.Ready) ReportReady(true);
                else if (upload.Interaction == WebUploadInteraction.Login) ReportReady(false);
                UploadNotice.Text = upload.Status;
                UploadProgress.IsIndeterminate = upload.Progress is null;
                if (upload.Progress is { } progress) UploadProgress.Value = progress;
                ContinueButton.IsEnabled = !upload.SubmissionStarted;
                LaterButton.Content = upload.SubmissionStarted ? "结束等待" : "稍后发送";
                PublishUpload();
                if (upload.Result is null)
                {
                    if (upload.Interaction != WebUploadInteraction.None) PresentInteraction(upload.Interaction, upload.Status);
                    else
                    {
                        presentedInteraction = WebUploadInteraction.None;
                        if (upload.Ready && returnAfterAmazonLogin) { returnAfterAmazonLogin = false; HideForBackground(); }
                    }
                }
                if (upload.Result is { } result) Complete(result);
            }
            else
            {
                var state = await endpoint.EvaluateAsync("""
                    (() => {
                        if (location.origin !== 'https://www.amazon.com' || location.pathname.replace(/\/$/,'') !== '/sendtokindle') return null;
                        const visible = e => !!e && e.getClientRects().length > 0;
                        if (visible(document.querySelector('#s2k-dnd-sign-in-button'))) return false;
                        return [...document.querySelectorAll('.s2k-dnd-add-your-files-button')].some(visible) ? true : null;
                    })()
                    """);
                if (state is "true" or "false")
                {
                    ReportReady(state == "true");
                    if (state == "true" && returnAfterAmazonLogin) { returnAfterAmazonLogin = false; HideForBackground(); }
                }
            }
        }
        catch
        {
            if (completion is not null && upload is not null)
            {
                if (upload.SubmissionStarted) Complete(upload.Interrupt());
                else { upload.PauseAutomation("暂时无法自动操作网页，可点击“继续自动上传”，或使用 Edge 继续。"); PresentInteraction(WebUploadInteraction.Manual, upload.Status); }
            }
            else BrowserStatus.Text = "暂时无法确认网页状态，请重新打开网页后继续。";
        }
        finally { polling = false; webActionGate.Release(); }
    }

    private void ReportReady(bool ready)
    {
        if (lastReady == ready) return;
        lastReady = ready; LoginReady?.Invoke(ready);
        if (ready) BrowserStatus.Text = service == BrowserService.Amazon ? "亚马逊已连接。超过邮件限制的书籍会自动使用网页发送。"
            : "koz.moe 已登录。选择漫画和卷数后，可使用网站的 Kindle 推送功能。";
    }

    private void Complete(WebUploadResult result)
    {
        if (completion is null || completion.Task.IsCompleted) return;
        UploadNotice.Text = result.Message;
        UploadProgress.IsIndeterminate = false;
        UploadProgress.Value = result.Outcome == WebUploadOutcome.Submitted ? 100 : UploadProgress.Value;
        ContinueButton.IsEnabled = LaterButton.IsEnabled = false;
        BrowserStatus.Text = result.Message; StatusChanged?.Invoke(result.Message);
        UploadUpdated?.Invoke(new(result.Message, result.Outcome == WebUploadOutcome.Submitted ? 100 : upload?.Progress,
            WebUploadInteraction.None, upload?.SubmissionStarted == true));
        if (result.Outcome == WebUploadOutcome.Submitted && backgroundUpload) HideForBackground();
        completion.TrySetResult(result);
    }

    private async void External_Click(object sender, RoutedEventArgs e)
    {
        if (service == BrowserService.Comic)
        {
            var target = lastComicQuery is null ? "https://koz.moe/login.php" : "https://koz.moe/list.php?s=" + Uri.EscapeDataString(lastComicQuery);
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch { BrowserStatus.Text = "暂时无法打开系统浏览器，请稍后重试。"; }
            return;
        }
        if (switchingBrowser) return;
        if (edge is not null) { edge.SetVisible(true); BrowserStatus.Text = "请在轻阅专用 Edge 窗口中继续。"; return; }
        if (upload?.SubmissionStarted == true)
        { BrowserStatus.Text = "当前网页可能已经提交这本书，请先查看亚马逊记录。结果确认后，可从发送队列重新发送。"; return; }
        switchingBrowser = true; ExternalButton.IsEnabled = false;
        var acquired = false;
        try
        {
            await webActionGate.WaitAsync(lifetime.Token); acquired = true;
            if (upload?.SubmissionStarted == true)
            { BrowserStatus.Text = "网页已经开始发送，请先确认亚马逊记录后再切换。"; return; }
            Core?.Stop();
            edge?.Dispose(); edge = null;
            edge = await EdgeEndpoint.OpenAsync(Home, lifetime.Token);
            endpoint = new(edge.EvaluateAsync, edge.CallAsync);
            navigationStarted = DateTimeOffset.Now;
            if (completion is not null) upload = new AmazonWebUpload(uploadPath!);
            returnAfterAmazonLogin = true; presentedInteraction = WebUploadInteraction.None;
            ShowError("请在轻阅打开的专用 Edge 窗口中完成亚马逊登录。上传进度和结果会回到轻阅显示。");
            poll.Start();
        }
        catch (Exception ex) { ShowError(ex.Message); endpoint = embeddedEndpoint; }
        finally { if (acquired) webActionGate.Release(); switchingBrowser = false; ExternalButton.IsEnabled = true; }
    }

    private string? uploadPath;
    public Task<WebUploadResult> SendAsync(string path) { uploadPath = path; return UploadAsync(path); }
    private void ShowBrowser()
    {
        Browser.Visibility = service == BrowserService.Comic ? Visibility.Visible : Visibility.Collapsed;
        AmazonBrowser.Visibility = service == BrowserService.Amazon ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ShowError(string message) { Browser.Visibility = AmazonBrowser.Visibility = Visibility.Collapsed; ErrorText.Text = message; ErrorCard.Visibility = Visibility.Visible; }
    private void PublishUpload()
    {
        if (upload is not null) UploadUpdated?.Invoke(new(upload.Status, upload.Progress, upload.Interaction, upload.SubmissionStarted));
    }
    private void PresentInteraction(WebUploadInteraction interaction, string message)
    {
        UploadNotice.Text = BrowserStatus.Text = message;
        UploadUpdated?.Invoke(new(message, upload?.Progress, interaction, upload?.SubmissionStarted == true));
        if (presentedInteraction == interaction) return;
        presentedInteraction = interaction;
        returnAfterAmazonLogin = interaction == WebUploadInteraction.Login;
        Show(); Activate(); edge?.SetVisible(true);
    }
    private void HideForBackground()
    {
        var restoreOwner = IsActive || edge?.IsForeground == true;
        edge?.SetVisible(false);
        Hide();
        if (restoreOwner) Owner?.Activate();
    }
    public async Task DeferUploadAsync()
    {
        if (completion is null || upload is null) return;
        try
        {
            await webActionGate.WaitAsync(lifetime.Token);
            try { if (completion is not null && upload is not null) { Complete(upload.Interrupt()); HideForBackground(); } }
            finally { webActionGate.Release(); }
        }
        catch (OperationCanceledException) { }
    }
    private void Back_Click(object sender, RoutedEventArgs e) { if (Core?.CanGoBack == true && completion is null) Core.GoBack(); }
    private async void Home_Click(object sender, RoutedEventArgs e)
    {
        if (completion is not null && upload?.SubmissionStarted == true) { BrowserStatus.Text = "正在发送，请等待接收确认后再返回首页。"; return; }
        try
        {
            if (edge is not null) { navigationStarted = DateTimeOffset.Now; await edge.NavigateAsync(Home); }
            else { await InitializeAsync(); Core!.Navigate(Home); }
        }
        catch { ShowError("暂时无法打开首页，请重新打开网页。"); }
    }
    private async void Reload_Click(object sender, RoutedEventArgs e) { await OpenAsync(); if (completion is not null && Core is not null && upload?.SubmissionStarted != true) { upload?.ResumeAutomation(); await HomeNavigateAsync(); } }
    private async Task HomeNavigateAsync()
    {
        navigationStarted = DateTimeOffset.Now;
        if (edge is not null) await edge.NavigateAsync(Home); else Core?.Navigate(Home);
    }
    private void Continue_Click(object sender, RoutedEventArgs e)
    { upload?.ResumeAutomation(); presentedInteraction = WebUploadInteraction.None; if (backgroundUpload) HideForBackground(); _ = PollAsync(); }
    private async void Later_Click(object sender, RoutedEventArgs e) => await DeferUploadAsync();
    private void Return_Click(object sender, RoutedEventArgs e)
    {
        if (completion is not null) { HideForBackground(); return; }
        if (service == BrowserService.Comic && lastReady != true)
        { comicSearchFlow = false; loginReturnRequested = true; Browser.CoreWebView2?.Navigate(Home); }
        HideForBackground();
    }
    private void WebForAll_Click(object sender, RoutedEventArgs e) => WebForAllChanged?.Invoke(WebForAllCheck.IsChecked == true);

    private void DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        if (service != BrowserService.Comic) { args.Cancel = true; return; }
        try
        {
            var path = LibraryFiles.ReserveDownload(args.ResultFilePath, args.DownloadOperation.MimeType);
            args.ResultFilePath = path; args.Handled = true;
            downloads.Add(args.DownloadOperation, path);
            args.DownloadOperation.StateChanged += DownloadStateChanged;
            BrowserStatus.Text = "正在下载 EPUB，完成后会自动交给轻阅检查和修复。";
        }
        catch (Exception ex) { args.Cancel = true; BrowserStatus.Text = ex.Message + " 如需直接推送，可使用网站的“推到 Kindle”。"; }
    }
    private async void DownloadStateChanged(object? sender, object args)
    {
        if (shutDown) return;
        if (sender is not CoreWebView2DownloadOperation operation || !downloads.TryGetValue(operation, out var path)
            || operation.State == CoreWebView2DownloadState.InProgress) return;
        if (operation.State == CoreWebView2DownloadState.Interrupted && operation.CanResume)
        { BrowserStatus.Text = "下载中断，请在网页下载列表中恢复。"; Browser.CoreWebView2.OpenDefaultDownloadDialog(); return; }
        operation.StateChanged -= DownloadStateChanged; downloads.Remove(operation);
        if (operation.State != CoreWebView2DownloadState.Completed) { BrowserStatus.Text = "下载尚未完成，未开始检查或发送。"; return; }
        try
        {
            await Task.Run(() => LibraryFiles.ValidateDownloadedEpub(path));
            BrowserStatus.Text = "EPUB 下载完成，已交给轻阅检查和修复。"; DownloadFinished?.Invoke(path);
        }
        catch { BrowserStatus.Text = "下载结果不是完整 EPUB，请在网站重新选择 EPUB 版本。"; }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (shutDown) return;
        e.Cancel = true;
        if (completion is not null && upload is not null) _ = DeferUploadAsync();
        HideForBackground();
    }
    public void Shutdown()
    {
        if (shutDown) return;
        if (completion is not null && upload is not null) Complete(upload.Interrupt());
        foreach (var operation in downloads.Keys.ToArray()) { try { operation.Cancel(); } catch { } }
        edge?.CloseDedicatedBrowser();
        shutDown = true; Close();
    }
}
