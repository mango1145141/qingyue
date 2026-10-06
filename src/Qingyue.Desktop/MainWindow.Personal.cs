using System.IO;
using System.Windows;

namespace EpubKindleFix;

public partial class MainWindow
{
    private readonly Dictionary<string, Task> metadataTasks = new();
    private async Task ReadImportedMetadataAsync(LibraryBook book)
    {
        try
        {
            var details = await Task.Run(() => EpubPreview.Read(book.SourcePath, book.Id));
            book.Title = details.Title; book.Author = details.Author; book.CoverPath = details.CoverPath;
            if (!closed) bookshelf.Save();
        }
        catch { /* Import remains available for repair even if optional metadata cannot be read. */ }
        finally { metadataTasks.Remove(book.Id); }
    }
    private BookshelfWindow? bookshelfWindow;
    private void Bookshelf_Click(object sender, RoutedEventArgs e) => OpenBookshelf(0);
    private void WishList_Click(object sender, RoutedEventArgs e) => OpenBookshelf(1);
    private void Queue_Click(object sender, RoutedEventArgs e) => OpenBookshelf(2);
    private void OpenBookshelf(int tab)
    {
        if (bookshelfWindow is not null) { bookshelfWindow.ShowTab(tab); bookshelfWindow.Activate(); return; }
        bookshelfWindow = new BookshelfWindow(bookshelf, readingProfile, tab, RetryBookAsync,
            async book =>
            {
                await SearchDiscoveryBookAsync(book);
            }, () => { recommendationPage = 0; RefreshReadingRecommendations(); }) { Owner = this };
        bookshelfWindow.Closed += (_, _) => bookshelfWindow = null;
        bookshelfWindow.Show();
    }
    private void WantToRead_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not BookRecommendationCard { Book: { } book } card) return;
        var index = readingProfile.WantToRead.FindIndex(w => w.Id == book.Id);
        var removed = index >= 0 ? readingProfile.WantToRead[index] : null;
        if (removed is not null) readingProfile.WantToRead.RemoveAt(index);
        else readingProfile.WantToRead.Insert(0, book);
        try { ReadingProfileStore.Save(readingProfile); }
        catch
        {
            if (removed is not null) readingProfile.WantToRead.Insert(index, removed);
            else readingProfile.WantToRead.Remove(book);
            ShowWishFeedback(removed is not null ? "未能取消想读，请稍后重试" : "想读清单未能保存，请稍后重试");
            return;
        }
        card.SetWanted(removed is null);
        ShowWishFeedback(removed is not null ? "已从想读中移除" : "已加入想读清单");
        bookshelfWindow?.RefreshBooks();
    }
    private readonly System.Windows.Threading.DispatcherTimer wishFeedbackTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private void ShowWishFeedback(string message)
    {
        ReadingDiscoveryNotice.Text = message;
        WishFeedbackText.Text = message;
        WishFeedbackToast.Visibility = Visibility.Visible;
        UiMotion.Reveal(WishFeedbackToast, 6);
        wishFeedbackTimer.Stop(); wishFeedbackTimer.Start();
    }
    private void RecommendationCard_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is BookRecommendationCard { Book: { } book } card)
            card.SetWanted(readingProfile.WantToRead.Any(w => w.Id == book.Id));
    }
    private void PreviewCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (currentBook is not null) new BookPreviewWindow(currentBook) { Owner = this }.ShowDialog();
    }
    private void SafeLibraryUpdate(LibraryBook book, string state, string error = "")
    {
        try { bookshelf.Update(book, state, error); }
        catch (Exception ex)
        {
            SettingsNotice.Text = "当前处理结果已显示，但书架未能保存：" + ex.Message;
            SettingsNotice.Visibility = Visibility.Visible;
        }
    }
    private async Task RetryScheduledAsync()
    {
        if (isBusy || closed || bookshelf.Paused) return;
        try
        {
        if (fileQueue.Count > 0 && settings.AutoRepair) { await ProcessQueueAsync(); return; }
        if (!settings.AutoSend) return;
        if (deliveryPausedByUser) return;
        foreach (var book in bookshelf.Books.Where(b => b.State == "发送失败" && b.RetryAt <= DateTimeOffset.Now && b.Report is not null).ToArray())
        {
            if (!File.Exists(book.Report!.OutputPath)) { book.RetryAt = null; SafeLibraryUpdate(book, "发送失败", "修复版已移动或删除，请重新选择原书检查。"); continue; }
            book.RetryAt = null; bookshelf.Update(book,"待发送");
            if (!sendQueue.Contains(book)) sendQueue.Enqueue(book);
        }
        if (sendQueue.Count > 0 && (HasCompleteMailSettings() || UseWebDelivery(sendQueue.Peek()))) await SendPendingQueueAsync(true);
        UpdateQueueText();
        }
        catch (Exception ex) { bookshelf.Paused = true; SettingsNotice.Text = "队列已暂停，无法保存处理记录：" + ex.Message; SettingsNotice.Visibility = Visibility.Visible; }
    }
    private async Task RetryBookAsync(LibraryBook book)
    {
        if (isBusy || closed) { SettingsNotice.Text = "当前正在处理书籍，请稍后再试。"; SettingsNotice.Visibility = Visibility.Visible; return; }
        try
        {
            if (book.State == "发送结果待确认" && MessageBox.Show(this,"上次发送结果不确定。请先查看 Kindle，确认需要重新发送后继续。", "确认重试", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
            bookshelf.Paused = false;
            deliveryPausedByUser = false;
            book.RetryAt = null; book.Attempts = 0;
            if (book.Report is null || !File.Exists(book.Report.OutputPath))
            {
                if (!File.Exists(book.SourcePath)) { SafeLibraryUpdate(book,"检查失败","原书已移动或删除，请重新选择文件。"); return; }
                book.Report = null; book.PreviewApproved = false;
                bookshelf.Update(book,"待检查"); if (!fileQueue.Contains(book)) fileQueue.Enqueue(book);
                await ProcessQueueAsync();
            }
            else
            {
                isBusy = true; SetControlsBusy(true);
                bool connected;
                try { connected = await EnsureDeliveryConnectedAsync(book); }
                finally { isBusy = false; SetControlsBusy(false); }
                if (!connected) return;
                bookshelf.Update(book,"待发送");
                var others = sendQueue.Where(b => !ReferenceEquals(b,book)).ToArray();
                sendQueue.Clear(); sendQueue.Enqueue(book); foreach (var other in others) sendQueue.Enqueue(other);
                isBusy = true; SetControlsBusy(true);
                try { await SendNextReportAsync(); }
                finally { isBusy = false; SetControlsBusy(false); }
            }
        }
        catch (Exception ex) { SettingsNotice.Text = "重试未能开始：" + ex.Message; SettingsNotice.Visibility = Visibility.Visible; }
        finally { UpdateQueueText(); }
    }
}
