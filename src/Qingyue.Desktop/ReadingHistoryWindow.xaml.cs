using System.Text.Json;
using System.Windows;

namespace EpubKindleFix;

public partial class ReadingHistoryWindow : Window
{
    private readonly ReadingProfile profile;
    public event Action? ProfileChanged;
    public ReadingHistoryWindow(ReadingProfile profile)
    {
        InitializeComponent();
        this.profile = profile;
        RefreshHistory();
    }

    private void RefreshHistory()
    {
        RememberSearchesCheck.IsChecked = profile.RememberSearches;
        SearchHistoryList.ItemsSource = profile.Searches.OrderByDescending(s => s.At).Take(100).ToList();
        EmptyHistoryText.Visibility = profile.Searches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Commit(Action<ReadingProfile> change)
    {
        var draft = JsonSerializer.Deserialize<ReadingProfile>(JsonSerializer.Serialize(profile))!;
        change(draft);
        try
        {
            ReadingProfileStore.Save(draft);
            profile.RememberSearches = draft.RememberSearches;
            profile.Searches = draft.Searches;
            profile.DismissedBooks = draft.DismissedBooks;
            HistoryNotice.Text = "已保存。";
            ProfileChanged?.Invoke();
        }
        catch { HistoryNotice.Text = "未能保存，请检查磁盘空间后重试。"; }
        RefreshHistory();
    }
    private void RememberSearches_Click(object sender, RoutedEventArgs e) => Commit(p => p.RememberSearches = RememberSearchesCheck.IsChecked == true);
    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "清空这台电脑的搜索记录，并重新显示已标记为不感兴趣的书？", "重置推荐", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Commit(p => { p.Searches.Clear(); p.DismissedBooks.Clear(); });
    }
    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
