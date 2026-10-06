using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace EpubKindleFix;

public partial class LibraryWindow : Window
{
    private Uri? website;
    private Task? initializing;
    private bool loadedPage;
    private bool shuttingDown;
    private bool isClosed;
    private int activeDownloads;
    private bool returnAfterLogin;
    private bool showHomeWhenLoggedIn;
    private string authPageToken = Guid.NewGuid().ToString("N");
    private ulong currentNavigationId;
    private string? pendingSearchQuery;
    private string? lastSearchQuery;
    private int searchRedirectRecoveries;
    private int loginStatusRecoveries;
    private bool recoveringLoginStatus;
    private bool includeEpubFilter = true;
    private bool formatFilterRecoveryUsed;
    private long navigationRequest;
    private bool searchFlow;
    private bool submittingSearch;
    private readonly Dictionary<CoreWebView2DownloadOperation, string> downloads = new();

    public event Action<string>? WebsiteChanged;
    public event Action<bool>? LoginReady;
    public event Action<string>? DownloadFinished;
    public event Action<string>? DownloadStatus;
    public event Action<string>? SearchSubmitted;

    public LibraryWindow(string initialUrl)
    {
        InitializeComponent();
        Title += " · " + typeof(LibraryWindow).Assembly.GetName().Version?.ToString(3);
        WebsiteBox.Text = LibraryFiles.MigrateWebsiteUrl(initialUrl);
        if (LibraryFiles.TryWebsite(WebsiteBox.Text, out var initial)) website = initial;
        Loaded += async (_, _) =>
        {
            // SearchAsync records its intent before Show raises Loaded. A first
            // search must not also start the default login/home navigation.
            if (website is not null && Browser.CoreWebView2 is null && !searchFlow) await OpenWebsiteAsync();
        };
        Closing += OnClosing;
        Closed += (_, _) => { isClosed = true; Browser.Dispose(); };
    }

    private Task InitializeBrowserAsync()
    {
        if (initializing?.IsFaulted == true || initializing?.IsCanceled == true) initializing = null;
        return initializing ??= InitializeCoreAsync();
    }

    private async Task InitializeCoreAsync()
    {
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KindleBookRepair", "LibraryBrowser-v2");
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
        await Browser.EnsureCoreWebView2Async(environment);
        var core = Browser.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsWebMessageEnabled = true;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.NavigationStarting += (_, args) =>
        {
            if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                args.Cancel = true;
                BrowserStatus.Text = "这次跳转不是安全网页，已停止。请在网站中继续选择书籍。";
            }
            else if (LibraryFiles.IsRetiredWebsite(uri))
            {
                args.Cancel = true;
                BrowserStatus.Text = "旧入口已停用，请使用新的书库网址。";
            }
            else if (LibraryFiles.IsLoginStatusPage(uri))
            {
                args.Cancel = true;
                RecoverLoginStatusPage();
            }
            else
            {
                currentNavigationId = args.NavigationId;
                AddressLabel.Text = uri.GetLeftPart(UriPartial.Path);
                authPageToken = Guid.NewGuid().ToString("N");
                loadedPage = false;
                FinishLoginButton.IsEnabled = false;
            }
        };
        core.NavigationCompleted += NavigationCompleted;
        core.WebMessageReceived += AuthMessageReceived;
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri)
                && (LibraryFiles.IsLoginStatusPage(uri) || LibraryFiles.IsRetiredWebsite(uri)))
                return;
            if (Uri.TryCreate(args.Uri, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps)
                core.Navigate(uri.AbsoluteUri);
        };
        core.DownloadStarting += DownloadStarting;
        core.ProcessFailed += (_, _) => BrowserStatus.Text = "网页组件暂时停止，请关闭软件后重新打开。";
    }

    private async Task OpenWebsiteAsync()
    {
        SetupError.Text = "";
        if (!LibraryFiles.TryWebsite(WebsiteBox.Text, out var candidate))
        {
            SetupError.Text = "请输入完整的 HTTPS 网站地址，例如你平时使用的 Z-Library 登录入口。";
            return;
        }
        OpenWebsiteButton.IsEnabled = false;
        var request = ++navigationRequest;
        searchFlow = false;
        showHomeWhenLoggedIn = true;
        returnAfterLogin = false;
        pendingSearchQuery = lastSearchQuery = null;
        loginStatusRecoveries = 0;
        recoveringLoginStatus = false;
        try
        {
            await InitializeBrowserAsync();
            if (isClosed || request != navigationRequest) return;
            website = candidate;
            WebsiteChanged?.Invoke(website!.AbsoluteUri);
            SetupCard.Visibility = Visibility.Collapsed;
            Browser.Visibility = Visibility.Visible;
            BrowserStatus.Text = "请在网站页面完成登录。账号、验证码和下载额度均由网站处理。";
            Browser.CoreWebView2.Navigate(website!.AbsoluteUri);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            if (isClosed || request != navigationRequest) return;
            initializing = null;
            SetupError.Text = "电脑还缺少微软网页组件。安装 WebView2 Runtime 后重新打开软件即可。";
            RuntimeButton.Visibility = Visibility.Visible;
        }
        catch
        {
            if (isClosed || request != navigationRequest) return;
            initializing = null;
            SetupError.Text = "暂时无法打开网页。请检查网络和网站地址后再试。";
        }
        finally { OpenWebsiteButton.IsEnabled = true; }
    }

    private async void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (isClosed || args.NavigationId != currentNavigationId) return;
        if (!args.IsSuccess)
        {
            if (args.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
            BrowserStatus.Text = "网页没有加载成功，请检查网络，或点击“更换网址”使用当前可用入口。";
            return;
        }
        loadedPage = true;
        RememberSearchFromNavigation();
        FinishLoginButton.IsEnabled = true;
        BackButton.IsEnabled = Browser.CoreWebView2.CanGoBack;
        recoveringLoginStatus = false;
        try
        {
            // Inspect only the shape of a mistaken JSON document. No token or account details cross into the app.
            var jsonDocument = await Browser.ExecuteScriptAsync("""
                (() => {
                  const text = (document.body?.innerText || '').trim();
                  if (!text.startsWith('{') || text.length > 8000) return false;
                  try { const d = JSON.parse(text); return typeof d.authenticated === 'boolean' && 'user' in d; }
                  catch (_) { return false; }
                })()
                """);
            if (isClosed || args.NavigationId != currentNavigationId) return;
            if (jsonDocument == "true") { RecoverLoginStatusPage(); return; }
            await RecoverFormatValidationAsync(args.NavigationId);
            if (isClosed || args.NavigationId != currentNavigationId) return;
            await ProbeLoginAsync();
            if (!isClosed && args.NavigationId == currentNavigationId) await SubmitPendingSearchAsync();
        }
        catch { BrowserStatus.Text = "网页暂时无法继续，请返回书库首页后重试搜索。"; }
    }

    private async Task ProbeLoginAsync()
    {
        if (website is null || Browser.CoreWebView2 is null || !loadedPage) return;
        if (!Uri.TryCreate(Browser.CoreWebView2.Source, UriKind.Absolute, out var current)
            || current.GetLeftPart(UriPartial.Authority) != website.GetLeftPart(UriPartial.Authority))
        {
            BrowserStatus.Text = "请完成网站登录并返回书库页面，再点击返回主页。";
            returnAfterLogin = false;
            return;
        }
        try
        {
            // Only send a boolean auth result to the app; cookies, credentials and account details stay in the browser.
            var probeScript = """
                (() => {
                  let authenticated = false, known = false;
                  const bootstrap = window.__authStatusBootstrap;
                  if (bootstrap && typeof bootstrap.authenticated === 'boolean') {
                    known = true;
                    authenticated = bootstrap.authenticated === true && !!bootstrap.user;
                  } else if (Object.prototype.hasOwnProperty.call(window, 'authUser')) {
                    known = true;
                    authenticated = !!window.authUser && typeof window.authUser === 'object';
                  }
                  if (!known) {
                    authenticated = [...document.querySelectorAll('a[href]')].some(a =>
                      /\/(?:users\/)?logout(?:\.php)?(?:[/?#]|$)/i.test(a.getAttribute('href') || ''));
                    known = authenticated;
                  }
                  if (!known) {
                    known = [...document.querySelectorAll('a[href]')].some(a =>
                      /\/(?:login|signin)(?:\.php)?(?:[/?#]|$)/i.test(a.getAttribute('href') || '') &&
                      a.getClientRects().length > 0);
                  }
                  window.chrome.webview.postMessage({type:'library-auth', token:__PAGE_TOKEN__, authenticated, known});
                })();
                """;
            await Browser.ExecuteScriptAsync(probeScript.Replace("__PAGE_TOKEN__", JsonSerializer.Serialize(authPageToken)));
        }
        catch { BrowserStatus.Text = "暂时无法确认登录状态，请稍后再试。"; returnAfterLogin = false; }
    }

    private void AuthMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (website is null || !loadedPage || !Uri.TryCreate(args.Source, UriKind.Absolute, out var source)
            || source.GetLeftPart(UriPartial.Authority) != website.GetLeftPart(UriPartial.Authority)) return;
        try
        {
            if (args.WebMessageAsJson.Length > 1024) return;
            using var message = JsonDocument.Parse(args.WebMessageAsJson);
            var root = message.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "library-auth"
                || !root.TryGetProperty("token", out var token) || token.GetString() != authPageToken
                || !root.TryGetProperty("authenticated", out var authenticated)
                || authenticated.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return;
            var ready = authenticated.GetBoolean();
            if (!root.TryGetProperty("known", out var known) || known.ValueKind != JsonValueKind.True)
            {
                if (returnAfterLogin) BrowserStatus.Text = "暂时无法确认登录，请在网站页面完成登录后再点击返回主页。";
                returnAfterLogin = false;
                return;
            }
            LoginReady?.Invoke(ready);
            if (ready)
            {
                if (searchFlow)
                    BrowserStatus.Text = "登录已确认，正在继续本次书籍搜索。";
                else if (returnAfterLogin || showHomeWhenLoggedIn)
                    BrowserStatus.Text = "登录已确认。返回主页输入书名，即可搜索并选择 EPUB。";
                if (!searchFlow && pendingSearchQuery is null && (returnAfterLogin || showHomeWhenLoggedIn))
                {
                    showHomeWhenLoggedIn = false;
                    Hide();
                    Owner?.Activate();
                }
            }
            else if (returnAfterLogin)
                BrowserStatus.Text = "尚未确认登录成功。请先在网站页面登录，完成后再点击“登录完成，返回主页”。";
            returnAfterLogin = false;
        }
        catch { /* Ignore unrelated or malformed page messages. */ }
    }

    private async void FinishLogin_Click(object sender, RoutedEventArgs e)
    {
        if (!loadedPage) return;
        returnAfterLogin = true;
        BrowserStatus.Text = "正在确认网站登录状态…";
        await ProbeLoginAsync();
    }

    public async Task SearchAsync(string query)
    {
        if (website is null) throw new InvalidOperationException("请先连接 Z-Library 网站。");
        var request = ++navigationRequest;
        searchFlow = true;
        showHomeWhenLoggedIn = false;
        returnAfterLogin = false;
        pendingSearchQuery = lastSearchQuery = query.Trim();
        searchRedirectRecoveries = 0;
        loginStatusRecoveries = 0;
        recoveringLoginStatus = false;
        includeEpubFilter = true;
        formatFilterRecoveryUsed = false;
        Show();
        Activate();
        await InitializeBrowserAsync();
        if (isClosed || request != navigationRequest) return;
        SetupCard.Visibility = Visibility.Collapsed;
        Browser.Visibility = Visibility.Visible;
        BrowserStatus.Text = "正在打开书库并提交搜索…";
        Browser.CoreWebView2.Navigate(website.AbsoluteUri);
    }

    private async Task SubmitPendingSearchAsync()
    {
        if (submittingSearch || pendingSearchQuery is null || website is null || Browser.CoreWebView2 is null || !loadedPage) return;
        if (!Uri.TryCreate(Browser.CoreWebView2.Source, UriKind.Absolute, out var current)
            || current.GetLeftPart(UriPartial.Authority) != website.GetLeftPart(UriPartial.Authority)) return;
        var query = pendingSearchQuery;
        var page = currentNavigationId;
        var request = navigationRequest;
        var script = """
            (() => {
              const form = document.querySelector('form#searchForm') ||
                [...document.querySelectorAll('form')].find(f => f.querySelector('input[name="q"]') &&
                  /\/s\/?$/i.test(new URL(f.action, location.href).pathname));
              if (!form) return 'missing-form';
              const action = new URL(form.action, location.href);
              if (action.origin !== location.origin || /\/api\//i.test(action.pathname)) return 'invalid-form';
              form.target = '_self';
              const input = form.querySelector('input[name="q"]') || form.querySelector('input[type="search"]');
              if (!input) return 'missing-form';
              const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
              setter.call(input, __SEARCH_QUERY__);
              input.disabled = false;
              input.dispatchEvent(new Event('input', {bubbles:true}));
              input.dispatchEvent(new Event('change', {bubbles:true}));
              const controls = [...form.querySelectorAll('[name="extensions[]"], [name="extensions"]')];
              const nativeValues = controls.filter(e => !e.hasAttribute('data-kindle-format')).flatMap(e => e instanceof HTMLSelectElement
                ? [...e.options].map(o => o.value) : [e.value]);
              // Preserve the website's exact enum spelling and fall back to its usual EPUB label.
              const epubValue = nativeValues.find(v => typeof v === 'string' && v.toUpperCase() === 'EPUB') || 'EPUB';
              controls.forEach(e => e.disabled = true);
              form.querySelector('input[data-kindle-format]')?.remove();
              if (__USE_EPUB_FILTER__) {
                const extension = document.createElement('input');
                extension.type = 'hidden'; extension.name = 'extensions[]';
                extension.dataset.kindleFormat = 'true'; extension.value = epubValue;
                form.append(extension);
              }
              const submitter = form.querySelector('button[type="submit"], input[type="submit"]');
              if (submitter) submitter.formTarget = '_self';
              // Defer navigation until ExecuteScriptAsync has returned its status to the host.
              setTimeout(() => {
                if (submitter) submitter.click();
                else if (typeof form.requestSubmit === 'function') form.requestSubmit();
                else HTMLFormElement.prototype.submit.call(form);
              }, 0);
              return 'submitted';
            })()
            """;
        // Clear before the deferred submit so another completed navigation cannot send the query twice.
        pendingSearchQuery = null;
        submittingSearch = true;
        try
        {
            var result = await Browser.ExecuteScriptAsync(script
                .Replace("__USE_EPUB_FILTER__", includeEpubFilter ? "true" : "false")
                .Replace("__SEARCH_QUERY__", JsonSerializer.Serialize(query)));
            if (isClosed || request != navigationRequest) return;
            var status = JsonSerializer.Deserialize<string>(result);
            if (status == "submitted") SearchSubmitted?.Invoke(query);
            else if (pendingSearchQuery is null && lastSearchQuery == query) pendingSearchQuery = query;
            if (currentNavigationId != page) return;
            BrowserStatus.Text = status == "submitted"
                ? includeEpubFilter ? "已提交搜索，请在网站结果中选择 EPUB 版本下载。"
                    : "已保留书名并重新搜索，请在结果中选择 EPUB 版本下载。"
                : "搜索词已保留。请完成网站登录，返回书库后会继续搜索；也可更换可用入口。";
        }
        catch
        {
            if (!isClosed && request == navigationRequest && lastSearchQuery == query && pendingSearchQuery is null)
                pendingSearchQuery = query;
            throw;
        }
        finally { submittingSearch = false; }
    }

    private async Task RecoverFormatValidationAsync(ulong navigationId)
    {
        if (lastSearchQuery is null || formatFilterRecoveryUsed || website is null
            || Browser.CoreWebView2 is null || !Uri.TryCreate(Browser.CoreWebView2.Source, UriKind.Absolute, out var current)
            || current.GetLeftPart(UriPartial.Authority) != website.GetLeftPart(UriPartial.Authority)) return;
        var rejected = await Browser.ExecuteScriptAsync("""
            (() => /(?:the\s+)?selected\s+extensions(?:\.\d+)?\s+is\s+invalid\.?/i
              .test(document.body?.innerText || ''))()
            """);
        if (isClosed || currentNavigationId != navigationId || rejected != "true") return;
        formatFilterRecoveryUsed = true;
        includeEpubFilter = false;
        pendingSearchQuery = lastSearchQuery;
        BrowserStatus.Text = "网站没有接受格式筛选，正在保留书名重新搜索…";
        DownloadStatus?.Invoke(BrowserStatus.Text);
    }

    private void RememberSearchFromNavigation()
    {
        if (website is null || Browser.CoreWebView2 is null
            || !Uri.TryCreate(Browser.CoreWebView2.Source, UriKind.Absolute, out var current)
            || !string.Equals(current.GetLeftPart(UriPartial.Authority), website.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
            || !(current.AbsolutePath.TrimEnd('/').Equals("/s", StringComparison.OrdinalIgnoreCase)
                || current.AbsolutePath.StartsWith("/s/", StringComparison.OrdinalIgnoreCase))) return;
        // Read only the submitted book query on the configured library's search route.
        // Account, cookies, authentication endpoints, and other query parameters are excluded.
        try
        {
            foreach (var part in current.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                if (pair.Length != 2 || Uri.UnescapeDataString(pair[0]) != "q") continue;
                var query = Uri.UnescapeDataString(pair[1].Replace('+', ' ')).Trim();
                if (query.Length is > 0 and <= 300) SearchSubmitted?.Invoke(query);
                return;
            }
        }
        catch { /* Unsupported or malformed URL does not become search history. */ }
    }

    private void RecoverLoginStatusPage()
    {
        if (website is null || Browser.CoreWebView2 is null || recoveringLoginStatus) return;
        if (loginStatusRecoveries++ >= 2)
        {
            pendingSearchQuery = null;
            loadedPage = false;
            FinishLoginButton.IsEnabled = false;
            Browser.Visibility = Visibility.Collapsed;
            SetupCard.Visibility = Visibility.Visible;
            SetupError.Text = "网站反复跳到登录检查页面，搜索尚未完成。请重新打开登录页或更换可用入口。";
            DownloadStatus?.Invoke(SetupError.Text);
            return;
        }
        recoveringLoginStatus = true;
        if (lastSearchQuery is not null) showHomeWhenLoggedIn = false;
        returnAfterLogin = false;
        if (lastSearchQuery is not null && searchRedirectRecoveries++ == 0)
            pendingSearchQuery = lastSearchQuery;
        else pendingSearchQuery = null;
        BrowserStatus.Text = "网站跳到了登录状态页面，正在返回书库。";
        DownloadStatus?.Invoke("正在纠正网站跳转，返回书库后继续搜索。");
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!isClosed) Browser.CoreWebView2.Navigate(website.AbsoluteUri);
        }));
    }

    private void DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        var operation = args.DownloadOperation;
        string path;
        try { path = LibraryFiles.ReserveDownload(args.ResultFilePath, operation.MimeType); }
        catch (Exception ex)
        {
            args.Cancel = true;
            BrowserStatus.Text = ex is InvalidDataException ? ex.Message : "无法在 G 盘保存下载，请检查磁盘空间和文件夹权限。";
            DownloadStatus?.Invoke(BrowserStatus.Text);
            return;
        }
        args.ResultFilePath = path;
        args.Handled = true;
        downloads.Add(operation, path);
        activeDownloads++;
        DownloadProgress.Visibility = Visibility.Visible;
        operation.BytesReceivedChanged += DownloadBytesChanged;
        operation.StateChanged += DownloadStateChanged;
        BrowserStatus.Text = "开始下载：" + Path.GetFileName(path);
        DownloadStatus?.Invoke(BrowserStatus.Text);
    }

    private void DownloadBytesChanged(object? sender, object args)
    {
        if (sender is not CoreWebView2DownloadOperation operation || !downloads.TryGetValue(operation, out var path)) return;
        var total = operation.TotalBytesToReceive ?? 0;
        DownloadProgress.IsIndeterminate = total <= 0;
        if (total > 0) DownloadProgress.Value = 100.0 * operation.BytesReceived / total;
        BrowserStatus.Text = $"正在下载 {Path.GetFileName(path)} · {operation.BytesReceived / 1024.0 / 1024.0:F1} MB";
        DownloadStatus?.Invoke(BrowserStatus.Text);
    }

    private async void DownloadStateChanged(object? sender, object args)
    {
        if (sender is not CoreWebView2DownloadOperation operation || !downloads.TryGetValue(operation, out var path)
            || operation.State == CoreWebView2DownloadState.InProgress) return;
        if (operation.State == CoreWebView2DownloadState.Interrupted && operation.CanResume)
        {
            // Keep the browser's genuine resume control available, with no automatic retry loop.
            BrowserStatus.Text = "下载中断。可以在网页下载列表中恢复，完整下载前不会开始修复。";
            Browser.CoreWebView2.OpenDefaultDownloadDialog();
            DownloadStatus?.Invoke(BrowserStatus.Text);
            return;
        }
        downloads.Remove(operation);
        operation.BytesReceivedChanged -= DownloadBytesChanged;
        operation.StateChanged -= DownloadStateChanged;
        activeDownloads--;
        DownloadProgress.Visibility = activeDownloads > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (operation.State != CoreWebView2DownloadState.Completed)
        {
            BrowserStatus.Text = "下载未完成，尚未检查或发送。请在网站中重新下载。";
            DownloadStatus?.Invoke(BrowserStatus.Text);
            return;
        }
        try
        {
            await Task.Run(() => LibraryFiles.ValidateDownloadedEpub(path));
            BrowserStatus.Text = "下载完成，已交给软件检查和修复。";
            DownloadStatus?.Invoke(BrowserStatus.Text);
            DownloadFinished?.Invoke(path);
            if (activeDownloads == 0) { Hide(); Owner?.Activate(); }
        }
        catch
        {
            BrowserStatus.Text = "下载结果不是完整的 EPUB，尚未修复或发送。请在网站中重新选择 EPUB。";
            DownloadStatus?.Invoke(BrowserStatus.Text);
        }
    }

    private void ChangeWebsite_Click(object sender, RoutedEventArgs e)
    {
        if (activeDownloads > 0) { BrowserStatus.Text = "请等当前下载完成后再更换网址。"; return; }
        Browser.CoreWebView2?.Stop();
        Browser.Visibility = Visibility.Collapsed;
        SetupCard.Visibility = Visibility.Visible;
        SetupError.Text = "";
        FinishLoginButton.IsEnabled = false;
        showHomeWhenLoggedIn = false;
        returnAfterLogin = false;
        navigationRequest++; searchFlow = false;
        pendingSearchQuery = lastSearchQuery = null;
    }
    private async void OpenWebsite_Click(object sender, RoutedEventArgs e) => await OpenWebsiteAsync();
    private async void WebsiteBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await OpenWebsiteAsync(); }
    }
    private void Back_Click(object sender, RoutedEventArgs e) { if (Browser.CoreWebView2?.CanGoBack == true) Browser.CoreWebView2.GoBack(); }
    private void Return_Click(object sender, RoutedEventArgs e) { Hide(); Owner?.Activate(); }
    private void Runtime_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true }); }
        catch { SetupError.Text = "请在浏览器访问微软 WebView2 页面安装 Runtime。"; }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!shuttingDown) { e.Cancel = true; Hide(); Owner?.Activate(); }
    }
    public bool HasActiveDownloads => activeDownloads > 0;
    public void Shutdown()
    {
        shuttingDown = true;
        foreach (var operation in downloads.Keys.ToArray())
        {
            try { operation.Cancel(); } catch { }
        }
        if (!isClosed) Close();
    }
}
