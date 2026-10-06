using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EpubKindleFix;

public sealed class BookshelfWindow : Window
{
    private readonly PersonalLibrary library;
    private readonly ReadingProfile profile;
    private readonly TextBox query = new() { Margin = new Thickness(0,0,12,0), MinWidth = 240 };
    private readonly ComboBox filter = new() { MinWidth = 135, Margin = new Thickness(0,0,12,0) };
    private readonly ListBox list = new() { BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock hint = new() { FontSize = 11, Margin = new Thickness(0,12,0,0), TextWrapping = TextWrapping.Wrap };
    private readonly Button pause;
    private readonly Func<LibraryBook, Task> retry;
    private readonly Action<DiscoveryBook> search;
    private readonly Action profileChanged;
    private int tab;
    private int refreshRevision;
    public BookshelfWindow(PersonalLibrary library, ReadingProfile profile, int tab, Func<LibraryBook, Task> retry,
        Action<DiscoveryBook> search, Action profileChanged)
    {
        this.library = library; this.profile = profile; this.retry = retry; this.search = search; this.profileChanged = profileChanged; this.tab = tab;
        Title = "轻阅 · 我的书架"; Width = 870; Height = 730; MinWidth = 700; MinHeight = 530;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner; SetResourceReference(BackgroundProperty,"Canvas");
        var root = new Grid { Margin = new Thickness(28) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); Content = root;
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,20) };
        for (var i = 0; i < 3; i++) { var index = i; tabs.Children.Add(Button(new[] { "我的书架", "想读清单", "发送队列" }[i], () => { this.tab = index; Refresh(); }, true)); } root.Children.Add(tabs);
        var tools = new DockPanel { Margin = new Thickness(0,0,0,16) }; Grid.SetRow(tools,1); root.Children.Add(tools);
        pause = Button("暂停队列", () => { library.Paused = !library.Paused; library.Save(); }); pause.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(pause,Dock.Right); tools.Children.Add(pause); DockPanel.SetDock(filter,Dock.Right); tools.Children.Add(filter); tools.Children.Add(query);
        query.ToolTip = "按书名、作者或分类搜索"; query.TextChanged += (_, _) => RefreshList(); filter.SelectionChanged += (_, _) => RefreshList();
        list.SetResourceReference(BackgroundProperty,"Canvas"); Grid.SetRow(list,2); root.Children.Add(list); Grid.SetRow(hint,3); root.Children.Add(hint);
        library.Changed += RefreshList; Closed += (_, _) => { library.Changed -= RefreshList; refreshRevision++; };
        Refresh();
    }
    private Button Button(string text, Action action, bool quiet = false)
    {
        var button = new Button { Content = text, Margin = new Thickness(0,0,8,0), Padding = new Thickness(12,8,12,8), FontSize = 12 };
        if (quiet) button.Style = (Style)FindResource("QuietButton"); else button.Style = (Style)FindResource("SecondaryButton");
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { hint.Text = "未能完成：" + ex.Message; } }; return button;
    }
    private void Refresh()
    {
        Title = "轻阅 · " + new[] { "我的书架", "想读清单", "发送队列" }[tab];
        var selected = filter.SelectedItem as string;
        filter.ItemsSource = tab == 0 ? new[] { "全部", "收藏" }.Concat(library.Books.Select(b => b.Category).Distinct()).ToArray()
            : tab == 2 ? new[] { "未完成", "全部", "失败", "邮件已提交", "网页已提交" } : new[] { "全部" };
        filter.SelectedItem = selected; if (filter.SelectedIndex < 0) filter.SelectedIndex = 0;
        pause.Visibility = tab == 2 ? Visibility.Visible : Visibility.Collapsed;
        query.Text = ""; RefreshList();
    }
    public void ShowTab(int tab) { this.tab = tab; Refresh(); }
    public void RefreshBooks() => RefreshList();
    private void RefreshList()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(RefreshList); return; }
        if (list is null || filter is null || hint is null || query is null || pause is null) return;
        var viewRevision = ++refreshRevision;
        var offset = FindScroll(list)?.VerticalOffset ?? 0;
        list.Items.Clear(); pause.Content = library.Paused ? "继续队列" : "暂停队列";
        var q = query.Text.Trim(); var f = filter.SelectedItem as string ?? "全部";
        if (tab == 1)
        {
            foreach (var wish in profile.WantToRead.Where(b => (b.Title + b.Author + b.GenreLabel).Contains(q,StringComparison.OrdinalIgnoreCase)))
            {
                var image = new Image { Width = 64, Height = 96, Margin = new Thickness(0,0,18,0) };
                var actions = new StackPanel { Orientation = Orientation.Horizontal };
                actions.Children.Add(Button(wish.IsComic ? "去漫画搜索 ↗" : "去书库搜索 ↗", () => search(wish)));
                actions.Children.Add(Button("移出想读", () => { profile.WantToRead.Remove(wish); ReadingProfileStore.Save(profile); profileChanged(); RefreshList(); },true));
                list.Items.Add(Row(image,wish.Title,wish.Author + " · " + wish.GenreLabel,wish.Description,actions));
                _ = LoadCoverAsync(image,wish,viewRevision);
            }
            hint.Text = $"想读 {profile.WantToRead.Count} 本 · 在推荐封面翻开后点击“想读”，重启后仍然保留。";
        }
        else
        {
            var books = library.Books.Where(b => (b.Title + b.Author + b.Category).Contains(q,StringComparison.OrdinalIgnoreCase));
            books = books.Where(b => tab == 0 ? f == "全部" || f == "收藏" && b.Favorite || b.Category == f
                : f == "全部" || f == "未完成" && b.State is not ("邮件已提交" or "网页已提交" or "已跳过") || f == "失败" && b.State.Contains("失败") || f == b.State);
            foreach (var book in books)
            {
                var image = new Image { Width = 64, Height = 96, Margin = new Thickness(0,0,18,0), Source = File.Exists(book.CoverPath) ? BookPreviewWindow.ReadImage(book.CoverPath) : null };
                var actions = new WrapPanel();
                actions.Children.Add(Button("预览", () => new BookPreviewWindow(book) { Owner = this }.ShowDialog()));
                if (tab == 0)
                {
                    actions.Children.Add(Button(book.Favorite ? "★ 已收藏" : "☆ 收藏", () => { book.Favorite = !book.Favorite; library.Save(); },true));
                    var category = new TextBox { Text = book.Category, Width = 92, Padding = new Thickness(6), FontSize = 11, Margin = new Thickness(0,0,8,0), MaxLength = 30, ToolTip = "填写分类，再点保存" };
                    actions.Children.Add(category); actions.Children.Add(Button("保存分类", () => { book.Category = string.IsNullOrWhiteSpace(category.Text) ? "未分类" : category.Text.Trim(); library.Save(); },true));
                }
                var resend = book.State is "邮件已提交" or "网页已提交";
                var action = Button(resend ? "再次发送" : book.Report is null ? "重试检查" : "发送 / 重试", () =>
                {
                    if (resend && MessageBox.Show(this,"这本书已经提交过，确定再次发送？","再次发送",MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                    _ = retry(book);
                });
                action.IsEnabled = book.State is not ("发送中" or "检查中"); actions.Children.Add(action);
                if (tab == 2 && book.State is not ("发送中" or "检查中" or "邮件已提交" or "网页已提交" or "已跳过"))
                    actions.Children.Add(Button("跳过", () => { book.RetryAt = null; library.Update(book,"已跳过"); },true));
                list.Items.Add(Row(image,book.Title,book.Author + " · " + book.Category + " · " + book.State,book.Detail,actions,tab == 2 ? book.Progress : null));
            }
            var remaining = library.Books.Count(b => b.State is not ("邮件已提交" or "网页已提交" or "已跳过"));
            hint.Text = tab == 0 ? $"共 {library.Books.Count} 本 · 书架、封面和修复记录保存在 G:\\chatgpt\\轻阅数据\\书架"
                : $"{remaining} 本未完成 · 邮件连接失败最多自动重试 3 次；网页结果不确定时请先确认，可单本继续。暂停会在当前任务结束后生效。";
        }
        if (list.Items.Count == 0) list.Items.Add(new TextBlock { Text = tab == 1 ? "还没有想读的书，去推荐区收藏一本吧。" : "这里还没有书籍，选入 EPUB 后就会出现。", Margin = new Thickness(20), FontSize = 14 });
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(() => { if (viewRevision == refreshRevision) FindScroll(list)?.ScrollToVerticalOffset(offset); }));
    }
    private FrameworkElement Row(Image image, string title, string author, string detail, Panel actions, int? progress = null)
    {
        var grid = new DockPanel();
        image.Margin = new Thickness(0);
        var coverHost = new Grid { Width = 64, Height = 96, Margin = new Thickness(0,0,18,0), VerticalAlignment = VerticalAlignment.Top };
        var fallback = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(6), Child = new TextBlock { Text = "轻 阅\n\n暂无封面", FontSize = 11, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        fallback.SetResourceReference(Border.BackgroundProperty,"AccentSoft");
        var fallbackStyle = new Style(typeof(Border)); fallbackStyle.Setters.Add(new Setter(VisibilityProperty,Visibility.Collapsed));
        var missing = new System.Windows.Data.Binding("Source") { Source = image };
        var trigger = new DataTrigger { Binding = missing, Value = null }; trigger.Setters.Add(new Setter(VisibilityProperty,Visibility.Visible)); fallbackStyle.Triggers.Add(trigger); fallback.Style = fallbackStyle;
        coverHost.Children.Add(fallback); coverHost.Children.Add(image); grid.Children.Add(coverHost);
        var info = new StackPanel(); info.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap });
        info.Children.Add(new TextBlock { Text = author, FontSize = 11, Margin = new Thickness(0,7,0,0), TextWrapping = TextWrapping.Wrap });
        var text = new TextBlock { Text = detail, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxHeight = 70, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0,7,0,12) };
        text.SetResourceReference(TextBlock.ForegroundProperty,"Muted"); info.Children.Add(text);
        if (progress is not null) info.Children.Add(new ProgressBar { Value = progress.Value, Height = 3, Margin = new Thickness(0,0,0,12) });
        info.Children.Add(actions); grid.Children.Add(info);
        var row = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(18), Margin = new Thickness(0,0,0,10), Child = grid };
        row.Loaded += (_, _) => Appearance.ApplyWindow(row);
        return row;
    }
    private async Task LoadCoverAsync(Image image, DiscoveryBook book, int viewRevision)
    {
        try { var cover = await BookCoverStore.LoadAsync(book); if (viewRevision == refreshRevision) image.Source = cover; } catch { }
    }
    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        if (parent is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) if (FindScroll(VisualTreeHelper.GetChild(parent,i)) is { } found) return found;
        return null;
    }
}
