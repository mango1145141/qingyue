using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using System.Windows.Input;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows.Controls.Primitives;

namespace EpubKindleFix;

public partial class MainWindow : Window
{
    private readonly Queue<LibraryBook> fileQueue = new();
    private readonly Queue<LibraryBook> sendQueue = new();
    private readonly PersonalLibrary bookshelf = new();
    private LibraryBook? currentBook;
    private readonly System.Windows.Threading.DispatcherTimer queueTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private AppSettings settings;
    private EpubRepairReport? currentReport;
    private bool isBusy;
    private LibraryWindow? libraryWindow;
    private bool searchingLibrary;
    private bool syncRunning;
    private bool syncPaused;
    private bool closed;
    private readonly ReadingProfile readingProfile;
    private DateOnly readingDay = DailyReadingQuote.Today;
    private int recommendationPage;
    private readonly ObservableCollection<RecommendedBook> recommendationBooks = new();
    private RecommendationFeed? recommendationFeed;
    private CancellationTokenSource? recommendationCancellation;
    private int recommendationRevision;
    private bool loadingRecommendations;
    private bool hasMoreRecommendations = true;
    private bool recommendationNeedsRetry;
    private long recommendationScrollIntent;
    private long consumedRecommendationScrollIntent;
    private string? recommendedQuery;
    private DateTimeOffset recommendedQueryAt;
    private readonly System.Windows.Threading.DispatcherTimer readingTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly System.Windows.Threading.DispatcherTimer syncTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    public MainWindow() : this(ReadingProfileStore.Load(), AppSettingsStore.Load(), true) { }

    internal MainWindow(ReadingProfile readingProfile, AppSettings settings, bool backgroundServices)
    {
        this.readingProfile = readingProfile;
        this.settings = settings;
        syncPaused = !backgroundServices;
        InitializeComponent();
        MainScroll.AddHandler(ScrollBar.ScrollEvent, new ScrollEventHandler(MainScroll_ScrollBarInput), true);
        Title += " · " + typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 24);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 24);
        UpdateConnectionBadge();
        UpdateWebButtons();
        RestoreLibrarySearch();
        RefreshDailyReading();
        RecommendationCards.ItemsSource = recommendationBooks;
        RefreshReadingRecommendations();
        foreach (var book in bookshelf.Books.OrderBy(b => b.AddedAt))
        {
            if (book.State == "待检查") fileQueue.Enqueue(book);
            else if (book.State is "待发送" && book.Report is not null) sendQueue.Enqueue(book);
        }
        UpdateQueueText();
        queueTimer.Tick += async (_, _) => await RetryScheduledAsync();
        wishFeedbackTimer.Tick += async (_, _) => { wishFeedbackTimer.Stop(); await UiMotion.HideAsync(WishFeedbackToast); };
        Closed += (_, _) => wishFeedbackTimer.Stop();
        if (backgroundServices) queueTimer.Start();
        Closing += MainWindow_Closing;
        Closed += (_, _) => { closed = true; recommendationCancellation?.Cancel(); syncTimer.Stop(); readingTimer.Stop(); queueTimer.Stop(); libraryWindow?.Shutdown(); amazonWindow?.Shutdown(); comicWindow?.Shutdown(); };
        readingTimer.Tick += (_, _) =>
        {
            if (readingDay != DailyReadingQuote.Today)
            {
                recommendationPage = 0;
                RefreshDailyReading();
                RefreshReadingRecommendations();
            }
        };
        if (backgroundServices) readingTimer.Start();
        syncTimer.Tick += async (_, _) => await SyncIdleAsync();
        Loaded += async (_, _) => { Appearance.Configure(settings); if (backgroundServices) { syncTimer.Start(); await SyncIdleAsync(); } };
        Activated += async (_, _) =>
        {
            if (readingDay != DailyReadingQuote.Today) { recommendationPage = 0; RefreshDailyReading(); RefreshReadingRecommendations(); }
            await SyncIdleAsync();
        };
    }

    private void ChooseButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "选择 EPUB 电子书",
            Filter = "EPUB 电子书 (*.epub)|*.epub",
            Multiselect = true,
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) == true) QueueFiles(picker.FileNames);
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e) => SetDropEffect(e);
    private void DropZone_DragOver(object sender, DragEventArgs e) => SetDropEffect(e);

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = (Brush)FindResource("Line");
        DropZone.Background = (Brush)FindResource("Surface");
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = (Brush)FindResource("Line");
        DropZone.Background = (Brush)FindResource("Surface");
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) QueueFiles(paths);
    }

    private void SetDropEffect(DragEventArgs e)
    {
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        DropZone.BorderBrush = (Brush)FindResource(hasFiles ? "Accent" : "Line");
        DropZone.Background = hasFiles ? (Brush)FindResource("AccentSoft") : (Brush)FindResource("Surface");
    }

    private void QueueFiles(IEnumerable<string> paths)
    {
        var accepted = new List<string>();
        var rejected = new List<string>();
        foreach (var path in paths)
        {
            if (File.Exists(path) && string.Equals(Path.GetExtension(path), ".epub", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var book = bookshelf.Add(path); fileQueue.Enqueue(book); accepted.Add(path);
                    if (!settings.AutoRepair) metadataTasks[book.Id] = ReadImportedMetadataAsync(book);
                }
                catch (Exception ex) { SettingsNotice.Text = "无法保存书架：" + ex.Message; SettingsNotice.Visibility = Visibility.Visible; }
            }
            else rejected.Add(path);
        }
        if (rejected.Count > 0)
            MessageBox.Show(this, "目前只支持 EPUB（.epub）文件。其他文件没有加入处理队列。", "文件格式不支持", MessageBoxButton.OK, MessageBoxImage.Information);
        UpdateQueueText();
        if (accepted.Count > 0 && !isBusy && settings.AutoRepair) _ = ProcessQueueAsync();
        else if (accepted.Count > 0 && !isBusy) { StatusText.Text = "书籍已加入队列，点击检查并修复。"; UpdateQueueText(); }
    }

    private async Task ProcessQueueAsync()
    {
        if (isBusy || bookshelf.Paused || closed) return;
        isBusy = true;
        SetControlsBusy(true);
        DropIllustration.Visibility = Visibility.Collapsed;
        HeroEyebrow.Visibility = Visibility.Collapsed;
        HeroSubtitle.Visibility = Visibility.Collapsed;
        HeroTitle.FontSize = 25;
        HeroIntro.Visibility = Visibility.Visible;
        HeroTitle.Visibility = Visibility.Visible;
        HeroTitle.Text = "让好书，直接抵达。";
        HeroTitle.Margin = new Thickness(0);
        HeroIntro.Margin = new Thickness(0, 8, 0, 22);
        MainScroll.ScrollToTop();
        DropHint.Visibility = Visibility.Collapsed;
        DropZone.MinHeight = 0;
        DropZone.Padding = new Thickness(20, 15, 20, 15);
        DropHeading.FontSize = 14;
        DropSubtitle.FontSize = 11;
        DropSubtitle.Margin = new Thickness(0, 6, 0, 12);
        DropHeading.Text = "再添一本，继续阅读。";
        DropSubtitle.Text = "继续拖入 EPUB，自动加入处理队列";
        ProgressCard.Visibility = Visibility.Visible;
        UiMotion.Reveal(ProgressCard);
        ReportCard.Visibility = Visibility.Collapsed;
        StatusIcon.Text = "";
        ProgressBar.IsIndeterminate = true;
        ProgressBar.Visibility = Visibility.Visible;

        try
        {
            var autoSendForBatch = !deliveryPausedByUser;
            var sendFailedForBatch = false;
            while (fileQueue.Count > 0 && !closed && !bookshelf.Paused)
            {
                var job = fileQueue.Dequeue();
                if (job.State != "待检查") continue;
                currentBook = job;
                var path = job.SourcePath;
                try { bookshelf.Update(job, "检查中"); }
                catch (Exception ex) { bookshelf.Paused = true; StatusText.Text = "队列已暂停，无法保存记录：" + ex.Message; fileQueue.Enqueue(job); job.State = "待检查"; break; }
                BookName.Text = Path.GetFileName(path);
                StatusText.Text = "正在检查 EPUB 结构和图片清单…";
                currentReport = null;
                UpdateQueueText();

                try
                {
                    if (metadataTasks.TryGetValue(job.Id, out var metadataTask)) await metadataTask;
                    var report = await Task.Run(() => EpubRepairer.Repair(path));
                    currentReport = report;
                    job.Report = report;
                    try
                    {
                        var details = await Task.Run(() => EpubPreview.Read(report.OutputPath, job.Id));
                        job.Title = details.Title; job.Author = details.Author; job.CoverPath = details.CoverPath;
                    }
                    catch { /* A book with unsupported preview markup can still be repaired and sent. */ }
                    bookshelf.Update(job, "待发送");
                    sendQueue.Enqueue(job);
                    ShowReport(report);
                    StatusText.Text = "修复完成，准备发送…";

                    if (autoSendForBatch && settings.AutoSend && !closed && !bookshelf.Paused)
                    {
                        sendFailedForBatch |= !await SendNextReportAsync(true);
                        if (lastDeliveryDeferred) autoSendForBatch = false;
                    }

                    if (autoSendForBatch && settings.AutoSend && sendQueue.Count > 0 && !closed && !bookshelf.Paused)
                    {
                        while (sendQueue.Count > 0 && !closed && !bookshelf.Paused)
                        {
                            if (!await SendNextReportAsync(true)) sendFailedForBatch = true;
                            if (lastDeliveryDeferred) { autoSendForBatch = false; break; }
                        }
                    }
                }
                catch (Exception ex)
                {
                    job.RetryAt = null; SafeLibraryUpdate(job, "检查失败", ex.Message);
                    StatusText.Text = "处理失败：" + ex.Message;
                    StatusIcon.Text = "!";
                    StatusIcon.Foreground = (Brush)FindResource("Warning");
                    ProgressBar.IsIndeterminate = false;
                    ProgressBar.Visibility = Visibility.Collapsed;
                    ReportCard.Visibility = Visibility.Collapsed;
                    QueueText.Text = "这本书检查失败，其他书籍会继续处理。可在发送队列中单本重试。";
                }
            }

            if (sendQueue.Count > 0)
            {
                ProgressCard.Visibility = Visibility.Visible;
                ProgressBar.Visibility = Visibility.Collapsed;
                StatusIcon.Text = sendFailedForBatch ? "!" : "✓";
                StatusIcon.Foreground = (Brush)FindResource(sendFailedForBatch ? "Warning" : "Success");
                StatusText.Text = sendFailedForBatch
                    ? "书籍已修复并保存，发送尚未完成。可在发送队列中查看结果并继续。"
                    : "书籍已修复并保存。发送时会按文件大小选择邮件或亚马逊网页。";
                SendButton.Visibility = Visibility.Visible;
                SendButton.IsEnabled = true;
            }
            else if (fileQueue.Count == 0 && currentReport is not null)
            {
                ProgressBar.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            isBusy = false;
            SetControlsBusy(false);
            UpdateQueueText();
        }
        if (fileQueue.Count > 0 && settings.AutoRepair && !bookshelf.Paused && !closed) _ = ProcessQueueAsync();
    }

    private async Task<bool> SendNextReportAsync(bool automatic = false)
    {
        lastDeliveryDeferred = false;
        if (sendQueue.Count == 0) return true;
        if (closed || bookshelf.Paused) return false;
        var job = sendQueue.Peek();
        if (job.State != "待发送" || job.Report is null) { sendQueue.Dequeue(); return true; }
        if (!await EnsureDeliveryConnectedAsync(job)) { lastDeliveryDeferred = true; deliveryPausedByUser = true; return false; }
        if (automatic && !settings.AutoSend) { lastDeliveryDeferred = true; return false; }
        if (closed || bookshelf.Paused) return false;
        sendQueue.Dequeue();
        var report = job.Report;
        currentBook = job;
        if (settings.PreviewBeforeSend && !job.PreviewApproved)
        {
            if (new BookPreviewWindow(job, true) { Owner = this }.ShowDialog() != true)
            {
                SafeLibraryUpdate(job, "等待预览"); UpdateQueueText();
                StatusText.Text = "修复版已保存，可以稍后在发送队列中预览并发送。";
                return true;
            }
            job.PreviewApproved = true;
        }
        if (closed) return false;
        try { job.Attempts++; job.RetryAt = null; bookshelf.Update(job, "发送中"); }
        catch (Exception ex) { SafeLibraryUpdate(job, "发送失败", "无法保存队列：" + ex.Message); return false; }
        currentReport = report;
        BookName.Text = Path.GetFileName(report.OutputPath);
        ShowReport(report);
        ProgressCard.Visibility = Visibility.Visible;
        ReportCard.Visibility = Visibility.Visible;
        UiMotion.Reveal(ReportCard);
        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = true;
        StatusIcon.Text = "";
        var webDelivery = UseWebDelivery(job);
        job.DeliveryChannel = webDelivery ? "网页" : "邮件";
        StatusText.Text = webDelivery ? "正在后台连接亚马逊…" : $"正在发送到 {settings.KindleEmail}…";
        amazonUploadActive = webDelivery;
        WebUploadActions.Visibility = webDelivery ? Visibility.Visible : Visibility.Collapsed;
        InspectUploadButton.Content = "查看发送"; DeferUploadButton.Content = "稍后发送";
        ProgressBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
        ProgressBar.Value = 0;
        SendButton.IsEnabled = false;
        SetControlsBusy(true);

        try
        {
            if (webDelivery)
            {
                var result = await GetAmazonWindow().SendAsync(report.OutputPath);
                if (result.Outcome != WebUploadOutcome.Submitted) throw new WebDeliveryFailure(result);
            }
            else await SendByEmailAsync(report.OutputPath);
            job.SentAt = DateTimeOffset.Now;
            var submitted = webDelivery ? "网页已提交" : "邮件已提交";
            job.Deliveries.Add(new(DateTimeOffset.Now, webDelivery ? "亚马逊账号书库" : settings.KindleEmail, submitted));
            SafeLibraryUpdate(job, submitted);
            StatusIcon.Text = "✓";
            StatusIcon.Foreground = (Brush)FindResource("Success");
            StatusText.Text = webDelivery ? "亚马逊已接收，等待转换与 Kindle 联网同步。" : settings.DeliveryMode == "Outlook"
                ? "已交给 Outlook 发送。请保持 Outlook 和 Kindle 联网，稍后等待同步。"
                : "邮件已提交。保持 Kindle 联网，稍后等待同步。";
            ProgressBar.Visibility = Visibility.Collapsed;
            SendButton.Visibility = sendQueue.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SendButton.IsEnabled = sendQueue.Count > 0;
            UpdateQueueText();
            return true;
        }
        catch (Exception ex)
        {
            var uncertain = ex is DeliveryFailure { Uncertain: true } or WebDeliveryFailure { Outcome: WebUploadOutcome.Uncertain };
            var deferred = ex is WebDeliveryFailure { Outcome: WebUploadOutcome.Deferred };
            lastDeliveryDeferred = deferred || webDelivery && uncertain;
            if (lastDeliveryDeferred) deliveryPausedByUser = true;
            job.RetryAt = ex is DeliveryFailure { SafeToRetry: true } && job.Attempts <= 3
                ? DateTimeOffset.Now.AddSeconds(Math.Pow(2, job.Attempts - 1) * 30) : null;
            var state = deferred ? "等待网页发送" : uncertain ? "发送结果待确认" : "发送失败";
            job.Deliveries.Add(new(DateTimeOffset.Now, webDelivery ? "亚马逊账号书库" : settings.KindleEmail, state));
            SafeLibraryUpdate(job, state, webDelivery ? ex.Message : EmailDelivery.FriendlyError(ex)
                + (uncertain ? " 请先确认 Kindle 是否收到，再决定是否重试。" : ""));
            StatusIcon.Text = "!";
            StatusIcon.Foreground = (Brush)FindResource("Warning");
            StatusText.Text = "发送暂未完成：" + job.Error + " 可在发送队列中重试。";
            ProgressBar.Visibility = Visibility.Collapsed;
            SendButton.Visibility = Visibility.Visible;
            SendButton.Content = "重试发送";
            SendButton.IsEnabled = true;
            UpdateQueueText();
            return false;
        }
        finally
        {
            amazonUploadActive = false;
            WebUploadActions.Visibility = Visibility.Collapsed;
            SetControlsBusy(false);
        }
    }

    private Task SendByEmailAsync(string filePath) => EmailDelivery.SendAsync(settings, filePath);

    private void ShowReport(EpubRepairReport report)
    {
        ImagesFoundText.Text = report.ImagesFound.ToString();
        EntriesAddedText.Text = report.ManifestEntriesAdded.ToString();
        var repaired = report.ManifestEntriesAdded == 0
            ? "图片清单完整，没有需要补登的图片。"
            : $"已找到 {report.ImagesFound} 张图片，补齐 {report.ManifestEntriesAdded} 个清单条目。";
        ReportText.Text = $"{repaired} EPUB 打包校验通过。";
        var warnings = new List<string>();
        if (report.WebpImages.Count > 0)
            warnings.Add($"发现 {report.WebpImages.Count} 张 WebP 图片，部分 Kindle 设备可能不支持显示。可先转换为 JPEG。示例：{string.Join("、", report.WebpImages.Take(2))}");
        if (report.MissingImageFiles.Count > 0)
            warnings.Add($"书内引用了压缩包中不存在的图片，补齐清单无法恢复这些文件：{string.Join("、", report.MissingImageFiles.Take(4))}");
        if (report.ImagesFound == 0) warnings.Add("没有找到图片资源；已完成 EPUB 结构和打包检查。");
        WarningText.Text = string.Join("\n", warnings);
        WarningText.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SavedPathText.Text = "已保存到：" + report.OutputPath;
        ToolTipService.SetToolTip(SavedPathText, report.OutputPath);
        ReportCard.Visibility = Visibility.Visible;
        UiMotion.Reveal(ReportCard);
        SendButton.Content = sendQueue.Count > 1 ? $"发送 {sendQueue.Count} 本到 Kindle" : "发送到 Kindle";
        SendButton.Visibility = sendQueue.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SendButton.IsEnabled = sendQueue.Count > 0;
    }

    private bool OpenSettings()
    {
        var dialog = new SettingsWindow(settings, draft =>
        {
            // Website events can arrive while the settings dialog is open.
            draft.LibraryUrl = settings.LibraryUrl;
            draft.RememberedLibraryUrl = settings.RememberedLibraryUrl;
            draft.AmazonWebConnected = settings.AmazonWebConnected;
            draft.RememberedComicLogin = settings.RememberedComicLogin;
            draft.PreferWebUpload = settings.PreferWebUpload;
            draft.ProtectedSyncCredential = settings.ProtectedSyncCredential;
            draft.SyncPendingJson = settings.SyncPendingJson;
            SettingsSync.RecordChanges(settings, draft);
            AppSettingsStore.Save(draft);
        }, readingProfile) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SavedSettings is null) return false;
        settings = dialog.SavedSettings;
        Appearance.Configure(settings);
        if (dialog.RecommendationChanged) { recommendationPage = 0; RefreshReadingRecommendations(); }
        UpdateConnectionBadge();
        SettingsNotice.Text = HasCompleteMailSettings() ? "邮箱已保存，可以自动发送。" : "邮箱已保存。首次发送前连接一次发件邮箱即可。";
        SettingsNotice.Visibility = Visibility.Visible;
        return true;
    }

    private bool HasEmailAddresses() => MailProviders.TryAddress(settings.SenderEmail, out _)
        && MailProviders.TryAddress(settings.KindleEmail, out _);

    private bool HasCompleteMailSettings() => HasEmailAddresses() && settings.IsSenderConnected;

    private async Task<bool> EnsureSenderConnectedAsync()
    {
        if (HasCompleteMailSettings()) return true;
        if (!HasEmailAddresses())
        {
            SettingsNotice.Text = "请先在设置中填写发件邮箱和 Kindle 接收邮箱。";
            SettingsNotice.Visibility = Visibility.Visible;
            return false;
        }
        SettingsNotice.Text = "正在检查可用的发件邮箱…";
        SettingsNotice.Visibility = Visibility.Visible;
        var draft = settings.ForAddresses(settings.SenderEmail, settings.KindleEmail, settings.AutoSend);
        try
        {
            if (!await SenderConnection.ConnectAsync(this, draft))
            {
                SettingsNotice.Text = "邮箱地址已保存。连接发件邮箱后，就能自动发送。";
                return false;
            }
            AppSettingsStore.Save(draft);
            settings = draft;
            UpdateConnectionBadge();
            SettingsNotice.Text = "发件邮箱已连接。以后拖入书籍即可自动发送。";
            return true;
        }
        catch
        {
            SettingsNotice.Text = "邮箱连接暂未完成或无法保存，请稍后重新连接。";
            return false;
        }
    }

    private void UpdateQueueText()
    {
        StartRepairButton.Visibility = fileQueue.Count > 0 && !isBusy && !settings.AutoRepair ? Visibility.Visible : Visibility.Collapsed;
        var pendingFiles = bookshelf.Books.Count(b => b.State is "待检查" or "检查中");
        var pendingSends = bookshelf.Books.Count(b => b.State is "待发送" or "发送中");
        var unresolved = bookshelf.Books.Count(b => b.State is "发送失败" or "检查失败" or "等待预览" or "等待网页发送" or "发送结果待确认");
        var count = pendingFiles + pendingSends + unresolved;
        QueueButton.Content = count > 0 ? $"发送队列 · {count}" : "发送队列";
        if (unresolved > 0 && sendQueue.Count == 0) { SendButton.Content = "查看待处理书籍"; SendButton.Visibility = Visibility.Visible; SendButton.IsEnabled = !isBusy; }
        QueueText.Text = count > 0 ? $"{pendingFiles} 本待检查 · {pendingSends} 本待发送 · {unresolved} 本需确认或重试" : "";
        QueueText.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (pendingSends > 1 && SendButton.Visibility == Visibility.Visible)
            SendButton.Content = $"发送 {pendingSends} 本到 Kindle";
    }

    private void SetControlsBusy(bool busy)
    {
        var disabled = busy || isBusy;
        ChooseButton.IsEnabled = !disabled;
        SettingsButton.IsEnabled = !disabled;
        SyncButton.IsEnabled = StartRepairButton.IsEnabled = !disabled;
        StartRepairButton.Visibility = fileQueue.Count > 0 && !disabled && !settings.AutoRepair ? Visibility.Visible : Visibility.Collapsed;
        if (disabled) SendButton.IsEnabled = false;
        else if (sendQueue.Count > 0) SendButton.IsEnabled = true;
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (isBusy) return;
        isBusy = true;
        SetControlsBusy(true);
        try { if (OpenSettings() && HasEmailAddresses()) await EnsureSenderConnectedAsync(); }
        finally { isBusy = false; SetControlsBusy(false); }
        await SyncIdleAsync();
        if (settings.AutoSend && (HasCompleteMailSettings() || settings.AmazonWebConnected) && sendQueue.Count > 0) { deliveryPausedByUser = false; await SendPendingQueueAsync(true); }
        if (fileQueue.Count > 0 && settings.AutoRepair && !bookshelf.Paused && !closed) _ = ProcessQueueAsync();
    }

    private async Task SendPendingQueueAsync(bool automatic = false)
    {
        if (isBusy || bookshelf.Paused || closed) return;
        isBusy = true;
        SetControlsBusy(true);
        try
        {
            while (sendQueue.Count > 0 && !closed && !bookshelf.Paused)
            {
                await SendNextReportAsync(automatic);
                if (lastDeliveryDeferred) break;
            }
        }
        finally
        {
            isBusy = false;
            SetControlsBusy(false);
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (isBusy) return;
        if (sendQueue.Count == 0) { OpenBookshelf(2); return; }
        bookshelf.Paused = false;
        deliveryPausedByUser = false;
        await SendPendingQueueAsync();
        if (fileQueue.Count > 0 && settings.AutoRepair && !bookshelf.Paused && !closed) _ = ProcessQueueAsync();
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (currentReport is null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(currentReport.OutputPath)}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法打开文件夹", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private LibraryWindow GetLibraryWindow()
    {
        if (libraryWindow is not null) return libraryWindow;
        libraryWindow = new LibraryWindow(settings.LibraryUrl) { Owner = this };
        libraryWindow.WebsiteChanged += url =>
        {
            var draft = settings.ForAddresses(settings.SenderEmail, settings.KindleEmail, settings.AutoSend);
            draft.LibraryUrl = url;
            if (!draft.ShowLibrarySearch) draft.RememberedLibraryUrl = "";
            settings = draft;
            RestoreLibrarySearch();
            try { AppSettingsStore.Save(draft); }
            catch { ShowLibraryNotice("网站本次可用，但网址未能保存到本机。下次打开可能需要重新填写。"); }
        };
        libraryWindow.LoginReady += ready =>
        {
            if (ready)
            {
                var needsSave = !settings.ShowLibrarySearch;
                settings.RememberedLibraryUrl = settings.LibraryUrl;
                RestoreLibrarySearch();
                if (needsSave)
                {
                    try { AppSettingsStore.Save(settings); }
                    catch
                    {
                        ShowLibraryNotice("本次可以搜索，但未能保存书库连接。下次打开可能需要重新连接。");
                        return;
                    }
                }
                ShowLibraryNotice("书库连接已记住，以后打开软件即可搜索。");
            }
            else
            {
                RestoreLibrarySearch();
                if (settings.ShowLibrarySearch)
                {
                    LibraryButton.Content = "重新登录 Z-Library";
                    ShowLibraryNotice("网站当前需要重新登录。请在书库网页中完成登录后继续，搜索栏会保留。");
                }
            }
        };
        libraryWindow.DownloadStatus += ShowLibraryNotice;
        libraryWindow.SearchSubmitted += RecordLibrarySearch;
        libraryWindow.DownloadFinished += path =>
        {
            ShowLibraryNotice("下载完成，正在接入检查和修复流程。原书已保存到 G 盘。");
            QueueFiles(new[] { path });
        };
        return libraryWindow;
    }

    private void RestoreLibrarySearch()
    {
        LibrarySearchCard.Visibility = settings.ShowLibrarySearch ? Visibility.Visible : Visibility.Collapsed;
        LibraryButton.Content = settings.ShowLibrarySearch ? "Z-Library" : "登录 Z-Library";
    }

    private void ShowLibraryNotice(string message)
    {
        LibraryNotice.Text = message;
        LibraryNotice.Visibility = Visibility.Visible;
        UiMotion.Reveal(LibraryNotice, 4);
    }

    private void LibraryButton_Click(object sender, RoutedEventArgs e)
    {
        var window = GetLibraryWindow();
        window.Show();
        window.Activate();
    }

    private async Task SearchLibraryAsync()
    {
        if (searchingLibrary) return;
        var query = LibrarySearchBox.Text.Trim();
        if (query.Length == 0) { ShowLibraryNotice("请输入书名、作者或 ISBN。"); LibrarySearchBox.Focus(); return; }
        if (query.Length > 300) { ShowLibraryNotice("搜索内容太长，请使用书名、作者或 ISBN。"); return; }
        searchingLibrary = true;
        LibrarySearchButton.IsEnabled = false;
        try { await GetLibraryWindow().SearchAsync(query); }
        catch { ShowLibraryNotice("暂时无法打开搜索，请检查网站连接后重试。"); }
        finally { searchingLibrary = false; LibrarySearchButton.IsEnabled = true; }
    }
    private async void LibrarySearch_Click(object sender, RoutedEventArgs e) => await SearchLibraryAsync();
    private async void LibrarySearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await SearchLibraryAsync(); }
    }

    private void RefreshDailyReading()
    {
        readingDay = DailyReadingQuote.Today;
        var quote = DailyReadingQuote.ForDay(readingDay);
        DailyQuoteText.Text = quote.Text;
        DailyQuoteAttribution.Text = quote.Attribution + " · " + readingDay.ToString("MM月dd日");
        DailyQuoteSourceButton.ToolTip = "查看原文出处 · 每天按北京时间更换";
        UiMotion.Reveal(DailyQuoteCard);
    }

    private void RefreshReadingRecommendations()
    {
        recommendationCancellation?.Cancel();
        recommendationCancellation?.Dispose();
        recommendationCancellation = new();
        recommendationRevision++;
        recommendationFeed = new(readingProfile, DailyReadingQuote.Today, recommendationPage);
        recommendationBooks.Clear();
        loadingRecommendations = false;
        hasMoreRecommendations = true;
        recommendationNeedsRetry = false;
        consumedRecommendationScrollIntent = recommendationScrollIntent;
        ReadingTasteText.Text = recommendationFeed.Summary;
        RecommendationShelfHint.Text = "每次 12 本，向下滑继续发现 · 悬停翻开，看看故事。";
        _ = LoadMoreRecommendationsAsync();
    }

    private async Task LoadMoreRecommendationsAsync()
    {
        if (closed || loadingRecommendations || !hasMoreRecommendations || recommendationFeed is null) return;
        var revision = recommendationRevision;
        var feed = recommendationFeed;
        var cancellation = recommendationCancellation!.Token;
        loadingRecommendations = true;
        RecommendationLoading.Visibility = Visibility.Visible;
        RecommendationLoadFooter.Visibility = Visibility.Collapsed;
        RecommendationEmptyText.Visibility = Visibility.Collapsed;
        try
        {
            var batch = await feed.NextAsync(cancellation);
            if (closed || revision != recommendationRevision || cancellation.IsCancellationRequested) return;
            ReadingTasteText.Text = feed.Summary;
            foreach (var book in batch.Books)
                if (!readingProfile.DismissedBooks.Contains(book.Book.Id)) recommendationBooks.Add(book);
            hasMoreRecommendations = batch.HasMore;
            recommendationNeedsRetry = batch.NeedsRetry || batch.Books.Count == 0 && batch.HasMore;
            RecommendationLoadStatus.Text = recommendationNeedsRetry ? "这次还没找到更多书，稍后再试"
                : hasMoreRecommendations ? "继续向下滑，发现下一批好书" : "这一轮已经看完，换一批继续发现";
            RecommendationRetryButton.Visibility = recommendationNeedsRetry ? Visibility.Visible : Visibility.Collapsed;
            RecommendationShelfHint.Text = $"已为你找到 {recommendationBooks.Count} 本 · 向下滑继续发现";
            RecommendationEmptyText.Visibility = recommendationBooks.Count == 0 && !hasMoreRecommendations ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            if (revision != recommendationRevision || closed) return;
            recommendationNeedsRetry = true;
            RecommendationLoadStatus.Text = "书目暂时无法加载，已显示的书仍然保留";
            RecommendationRetryButton.Visibility = Visibility.Visible;
        }
        finally
        {
            if (revision == recommendationRevision && !closed)
            {
                loadingRecommendations = false;
                consumedRecommendationScrollIntent = recommendationScrollIntent;
                RecommendationLoading.Visibility = Visibility.Collapsed;
                RecommendationLoadFooter.Visibility = Visibility.Visible;
            }
        }
    }

    private async void MainScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, MainScroll) && e.VerticalChange > 0)
            await AppendAfterScrollInputAsync();
    }
    private void MainScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta < 0) MarkRecommendationScrollInput();
    }
    private void MainScroll_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.End or Key.PageDown or Key.Down && e.OriginalSource is not TextBoxBase)
            MarkRecommendationScrollInput();
    }
    private void MainScroll_ScrollBarInput(object sender, ScrollEventArgs e)
    {
        if (e.OriginalSource is ScrollBar { Orientation: Orientation.Vertical }
            && (e.ScrollEventType is ScrollEventType.SmallIncrement or ScrollEventType.LargeIncrement or ScrollEventType.Last
                || e.ScrollEventType is ScrollEventType.ThumbTrack or ScrollEventType.ThumbPosition && e.NewValue > MainScroll.VerticalOffset))
            MarkRecommendationScrollInput();
    }
    private void MarkRecommendationScrollInput()
    {
        recommendationScrollIntent++;
        // Also covers a wheel gesture at the exact bottom, where no offset-change event occurs.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new Action(async () => await AppendAfterScrollInputAsync()));
    }
    private async Task AppendAfterScrollInputAsync()
    {
        if (closed || loadingRecommendations || recommendationNeedsRetry || MainScroll.ViewportHeight <= 0
            || recommendationScrollIntent <= consumedRecommendationScrollIntent) return;
        if (MainScroll.ExtentHeight - MainScroll.VerticalOffset - MainScroll.ViewportHeight >= 180) return;
        consumedRecommendationScrollIntent = recommendationScrollIntent;
        await LoadMoreRecommendationsAsync();
    }
    private async void LoadMoreRecommendations_Click(object sender, RoutedEventArgs e) => await LoadMoreRecommendationsAsync();

    private void RecordLibrarySearch(string query) => RecordReadingSearch(query, "book");
    private void RecordComicSearch(string query) => RecordReadingSearch(query, "comic");
    private void RecordReadingSearch(string query, string kind)
    {
        query = query.Trim();
        if (!readingProfile.RememberSearches || query.Length is 0 or > 300) return;
        var now = DateTimeOffset.UtcNow;
        var normalized = ReadingDiscovery.Normalize(query);
        if (normalized.Length == 0 || readingProfile.Searches.Any(s => s.Kind == kind && ReadingDiscovery.Normalize(s.Query) == normalized
            && now - s.At < TimeSpan.FromMinutes(10))) return;
        var fromRecommendation = recommendedQuery is not null && normalized == ReadingDiscovery.Normalize(recommendedQuery)
            && now - recommendedQueryAt < TimeSpan.FromMinutes(5);
        if (fromRecommendation) recommendedQuery = null;
        readingProfile.Searches.Insert(0, new ReadingSearch(query, now, fromRecommendation) { Kind = kind });
        readingProfile.Searches = readingProfile.Searches.OrderByDescending(s => s.At).Take(500).ToList();
        recommendationPage = 0;
        try { ReadingProfileStore.Save(readingProfile); ReadingDiscoveryNotice.Text = ""; }
        catch { ReadingDiscoveryNotice.Text = "本次推荐已更新，但搜索记录未能保存；请检查磁盘空间。"; }
        RefreshReadingRecommendations();
    }

    private async void SearchRecommendedBook_Click(object sender, RoutedEventArgs e)
    {
        var book = sender is BookRecommendationCard card ? card.Book : (sender as Button)?.Tag as DiscoveryBook;
        if (book is null) return;
        await SearchDiscoveryBookAsync(book);
    }

    private async Task SearchDiscoveryBookAsync(DiscoveryBook book)
    {
        if (book.IsComic ? searchingComic : searchingLibrary) return;
        recommendedQuery = book.Title;
        recommendedQueryAt = DateTimeOffset.UtcNow;
        if (book.IsComic)
        {
            ComicSearchBox.Text = book.Title;
            await SearchComicAsync();
            return;
        }
        LibrarySearchBox.Text = book.Title;
        await SearchLibraryAsync();
    }

    private void DismissRecommendedBook_Click(object sender, RoutedEventArgs e)
    {
        var book = sender is BookRecommendationCard card ? card.Book : (sender as Button)?.Tag as DiscoveryBook;
        if (book is null || readingProfile.DismissedBooks.Contains(book.Id)) return;
        readingProfile.DismissedBooks.Add(book.Id);
        try { ReadingProfileStore.Save(readingProfile); ReadingDiscoveryNotice.Text = "已记住，不再推荐这本书。"; }
        catch { readingProfile.DismissedBooks.Remove(book.Id); ReadingDiscoveryNotice.Text = "未能保存“不感兴趣”，请检查磁盘空间后重试。"; return; }
        var displayed = recommendationBooks.FirstOrDefault(b => b.Book.Id == book.Id);
        if (displayed is not null) recommendationBooks.Remove(displayed);
    }

    private void RefreshRecommendations_Click(object sender, RoutedEventArgs e)
    {
        recommendationPage = recommendationPage == int.MaxValue ? 0 : recommendationPage + 1;
        RefreshReadingRecommendations();
    }

    private void ReadingHistory_Click(object sender, RoutedEventArgs e)
    {
        var window = new ReadingHistoryWindow(readingProfile) { Owner = this };
        window.ProfileChanged += () => { recommendationPage = 0; ReadingDiscoveryNotice.Text = ""; RefreshReadingRecommendations(); };
        window.ShowDialog();
    }

    private void DailyQuoteSource_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(DailyReadingQuote.ForDay(readingDay).Url) { UseShellExecute = true }); }
        catch { ReadingDiscoveryNotice.Text = "暂时无法打开浏览器，请稍后再试。"; }
    }
    private void CoverSource_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://openlibrary.org/dev/docs/api/covers") { UseShellExecute = true }); }
        catch { ReadingDiscoveryNotice.Text = "暂时无法打开封面来源页面。"; }
    }
    private async void StartRepair_Click(object sender, RoutedEventArgs e) => await ProcessQueueAsync();
    private async Task SyncIdleAsync()
    {
        if (closed || isBusy || syncRunning || syncPaused || !SettingsSync.Connected(settings)) return;
        syncRunning = true;
        var original = settings;
        try
        {
            var next = await SettingsSync.SynchronizeAsync(original);
            // A dialog or repair may have started while the request was in flight.
            if (closed || isBusy || !ReferenceEquals(settings, original)) return;
            var changed = SettingsSync.Shared(settings) != SettingsSync.Shared(next);
            AppSettingsStore.Save(next); settings = next;
            UpdateConnectionBadge(); UpdateQueueText();
            SyncButton.ToolTip = "邮箱和偏好已同步";
            if (changed) { SettingsNotice.Text = "已同步手机上的邮箱和偏好。"; SettingsNotice.Visibility = Visibility.Visible; }
        }
        catch (SyncFailure failure)
        {
            if (failure.Status is System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.Unauthorized) syncPaused = true;
            SyncButton.ToolTip = failure.Message;
        }
        catch { SyncButton.ToolTip = "暂时无法同步，设置已保留在本机。"; }
        finally { syncRunning = false; }
        if (!closed && !isBusy && settings.AutoRepair && fileQueue.Count > 0) _ = ProcessQueueAsync();
    }
    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        if (isBusy) return;
        isBusy = true; SetControlsBusy(true);
        try
        {
            var dialog = new SyncWindow(settings, next =>
            {
                next.LibraryUrl = settings.LibraryUrl;
                next.RememberedLibraryUrl = settings.RememberedLibraryUrl;
                next.AmazonWebConnected = settings.AmazonWebConnected;
                next.RememberedComicLogin = settings.RememberedComicLogin;
                next.PreferWebUpload = settings.PreferWebUpload;
                AppSettingsStore.Save(next); settings = next;
            }) { Owner = this };
            dialog.ShowDialog(); syncPaused = false;
            UpdateConnectionBadge(); UpdateQueueText();
        }
        finally { isBusy = false; SetControlsBusy(false); }
        await SyncIdleAsync(); UpdateQueueText();
    }
    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (libraryWindow?.HasActiveDownloads != true && comicWindow?.HasActiveWork != true && amazonWindow?.HasActiveWork != true) return;
        var answer = MessageBox.Show(this, "仍有书籍正在下载或网页发送。退出会中断当前任务，发送结果可能需要确认。是否退出？", "任务尚未完成", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) e.Cancel = true;
    }

    private void UpdateConnectionBadge()
    {
        var connected = HasCompleteMailSettings() || settings.AmazonWebConnected;
        ConnectionBadge.Text = HasCompleteMailSettings() ? settings.AutoSend ? "邮箱已连接 · 大文件走网页" : "邮箱已连接 · 手动发送"
            : settings.AmazonWebConnected ? "亚马逊已连接 · 网页发送" : "邮箱 / 亚马逊待连接";
        ConnectionDot.Fill = (Brush)FindResource(connected ? "Success" : "Subtle");
        SetupHint.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Help_Click(object sender, RoutedEventArgs e)
    {
        HelpOverlay.Visibility = Visibility.Visible;
        UiMotion.Reveal(HelpOverlay, 0);
        HelpCloseButton.Focus();
    }
    private async void HelpClose_Click(object sender, RoutedEventArgs e)
    {
        await UiMotion.HideAsync(HelpOverlay);
        if (HelpOverlay.Visibility != Visibility.Collapsed) return;
        ChooseButton.Focus();
    }
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && HelpOverlay.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            await UiMotion.HideAsync(HelpOverlay);
            if (HelpOverlay.Visibility == Visibility.Collapsed) ChooseButton.Focus();
        }
    }
}
