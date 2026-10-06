using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EpubKindleFix;

internal sealed class WebDeliveryFailure(WebUploadResult result) : Exception(result.Message)
{
    public WebUploadOutcome Outcome { get; } = result.Outcome;
}

public partial class MainWindow
{
    private ServiceBrowserWindow? amazonWindow;
    private ServiceBrowserWindow? comicWindow;
    private bool searchingComic;
    private bool lastDeliveryDeferred;
    private bool deliveryPausedByUser;
    private bool amazonUploadActive;

    private bool UseWebDelivery(LibraryBook book) => settings.PreferWebUpload
        || (book.Report is not null && File.Exists(book.Report.OutputPath) && new FileInfo(book.Report.OutputPath).Length > AmazonWebUpload.MailLimit)
        || (!HasCompleteMailSettings() && settings.AmazonWebConnected);

    private async Task<bool> EnsureDeliveryConnectedAsync(LibraryBook book)
    {
        if (UseWebDelivery(book) || HasCompleteMailSettings()) return true;
        if (!HasEmailAddresses() && !OpenSettings()) return false;
        return await EnsureSenderConnectedAsync();
    }

    private ServiceBrowserWindow GetAmazonWindow()
    {
        if (amazonWindow is not null) return amazonWindow;
        amazonWindow = new(BrowserService.Amazon, settings.PreferWebUpload) { Owner = this };
        amazonWindow.LoginReady += ready =>
        {
            settings.AmazonWebConnected = ready;
            SaveWebSettings(); UpdateWebButtons(); UpdateConnectionBadge();
        };
        amazonWindow.WebForAllChanged += enabled =>
        {
            settings.PreferWebUpload = enabled; SaveWebSettings(); UpdateConnectionBadge();
        };
        amazonWindow.UploadUpdated += update =>
        {
            if (closed || !amazonUploadActive) return;
            StatusText.Text = update.Message;
            ProgressBar.Visibility = Visibility.Visible;
            ProgressBar.IsIndeterminate = update.Progress is null && update.Interaction == WebUploadInteraction.None;
            if (update.Progress is { } percent)
            {
                var next = Math.Clamp(percent, 0, 100);
                if (Appearance.Animate)
                {
                    var animation = new DoubleAnimation(ProgressBar.Value, next, TimeSpan.FromMilliseconds(320))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                    ProgressBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, animation);
                }
                else { ProgressBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null); ProgressBar.Value = next; }
            }
            InspectUploadButton.Content = update.Interaction == WebUploadInteraction.Login ? "完成登录 / 验证"
                : update.Interaction == WebUploadInteraction.Manual ? "查看并处理" : "查看发送";
            DeferUploadButton.Content = update.SubmissionStarted ? "结束等待" : "稍后发送";
            StatusIcon.Text = update.Interaction == WebUploadInteraction.None ? "" : "!";
            if (update.Interaction != WebUploadInteraction.None) StatusIcon.Foreground = (Brush)FindResource("Warning");
        };
        return amazonWindow;
    }

    private ServiceBrowserWindow GetComicWindow()
    {
        if (comicWindow is not null) return comicWindow;
        comicWindow = new(BrowserService.Comic) { Owner = this };
        comicWindow.LoginReady += ready =>
        {
            settings.RememberedComicLogin = ready; SaveWebSettings(); UpdateWebButtons();
        };
        comicWindow.DownloadFinished += path =>
        {
            if (closed) return;
            comicWindow.Hide(); Activate(); QueueFiles(new[] { path });
        };
        comicWindow.SearchSubmitted += RecordComicSearch;
        return comicWindow;
    }

    private void SaveWebSettings()
    {
        try { AppSettingsStore.Save(settings); }
        catch { SettingsNotice.Text = "网页连接状态暂时无法保存，本次会话仍可继续。"; SettingsNotice.Visibility = Visibility.Visible; }
    }

    private void UpdateWebButtons()
    {
        ComicButton.Content = settings.RememberedComicLogin ? "koz.moe 已连接" : "登录 koz.moe";
        ComicButton.ToolTip = "登录或管理漫画网站的 Kindle 推送";
        AmazonButton.Content = settings.AmazonWebConnected ? "亚马逊已连接" : "连接亚马逊";
        AmazonButton.ToolTip = "亚马逊官方上传 · 单本最高 200 MB · 登录后在主页后台发送";
        ComicConnectionText.Text = settings.RememberedComicLogin
            ? "漫画连接已记住。搜索后选择卷数，可使用网站的 Kindle 推送；登录过期时请重新登录。"
            : "在网站选择漫画和卷数，可直接推送到 Kindle；下载 EPUB 后也可交给轻阅修复。";
    }

    private async void AmazonButton_Click(object sender, RoutedEventArgs e) => await GetAmazonWindow().OpenAsync();
    private async void InspectUpload_Click(object sender, RoutedEventArgs e) => await GetAmazonWindow().OpenAsync();
    private async void DeferUpload_Click(object sender, RoutedEventArgs e)
    {
        DeferUploadButton.IsEnabled = false;
        try { if (amazonWindow is not null) await amazonWindow.DeferUploadAsync(); }
        finally { DeferUploadButton.IsEnabled = true; }
    }
    private async void ComicButton_Click(object sender, RoutedEventArgs e) => await GetComicWindow().OpenAsync(!settings.RememberedComicLogin);
    private async void ComicSearch_Click(object sender, RoutedEventArgs e) => await SearchComicAsync();
    private async void ComicSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await SearchComicAsync(); }
    }
    private async Task SearchComicAsync()
    {
        if (searchingComic) return;
        var query = ComicSearchBox.Text.Trim();
        if (query.Length == 0)
        { ComicNotice.Text = "请输入漫画名或作者。"; ComicNotice.Visibility = Visibility.Visible; ComicSearchBox.Focus(); return; }
        searchingComic = true; ComicSearchButton.IsEnabled = false;
        try { await GetComicWindow().SearchComicAsync(query); ComicNotice.Visibility = Visibility.Collapsed; }
        catch { ComicNotice.Text = "暂时无法打开漫画搜索，请检查网站连接后重试。"; ComicNotice.Visibility = Visibility.Visible; }
        finally { searchingComic = false; ComicSearchButton.IsEnabled = true; }
    }
}
