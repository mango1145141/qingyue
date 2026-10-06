using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace EpubKindleFix;

public sealed class BookPreviewWindow : Window
{
    private readonly LibraryBook book;
    private readonly TextBlock heading = new() { FontSize = 24, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox chapters = new() { DisplayMemberPath = "Title", Margin = new Thickness(0,0,0,12) };
    private readonly TextBox body = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, FontSize = 14, Padding = new Thickness(18), BorderThickness = new Thickness(0) };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0,12,0,0) };
    private readonly TextBlock author = new() { FontSize = 12, Margin = new Thickness(0,8,0,0) };
    private readonly Image cover = new() { Width = 76, Height = 108, Margin = new Thickness(0,0,20,0) };
    private readonly Button send;
    private int revision;
    public BookPreviewWindow(LibraryBook book, bool confirmSend = false)
    {
        this.book = book;
        body.Height = double.NaN;
        body.VerticalContentAlignment = VerticalAlignment.Top;
        Title = "轻阅 · 发送前预览"; Width = 820; Height = 740; MinWidth = 620; MinHeight = 530;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Canvas");
        var root = new Grid { Margin = new Thickness(28) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new DockPanel { Margin = new Thickness(0,0,0,18) }; header.Children.Add(cover);
        var info = new StackPanel(); heading.Text = book.Title; info.Children.Add(heading);
        info.Children.Add(author); info.Children.Add(summary);
        var log = $"加入书架：{book.AddedAt.ToLocalTime():yyyy-MM-dd HH:mm}\n状态：{book.State}\n原书：{book.SourcePath}\n修复版：{book.Report?.OutputPath ?? "尚未生成"}"
            + (book.Deliveries.Count == 0 ? "\n暂无发送记录" : "\n" + string.Join("\n",book.Deliveries.TakeLast(20).Reverse().Select(d => $"{d.At.ToLocalTime():MM-dd HH:mm} · {d.Result} · {d.Recipient}")));
        var history = new Expander { Header = "处理与发送记录", Margin = new Thickness(0,8,0,0), FontSize = 11,
            Content = new ScrollViewer { MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock { Text = log, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,8), FontSize = 11 } } };
        info.Children.Add(history); header.Children.Add(info); root.Children.Add(header);
        Grid.SetRow(chapters, 1); root.Children.Add(chapters); Grid.SetRow(body, 2); root.Children.Add(body);
        body.SetResourceReference(BackgroundProperty,"Surface"); body.SetResourceReference(ForegroundProperty,"Ink");
        var footer = new Grid { Margin = new Thickness(0,16,0,0) }; Grid.SetRow(footer, 3); root.Children.Add(footer);
        var left = new Button { Content = confirmSend ? "稍后发送" : "关闭", HorizontalAlignment = HorizontalAlignment.Left, Style = (Style)FindResource("QuietButton") };
        left.Click += (_, _) => { if (confirmSend) DialogResult = false; else Close(); }; footer.Children.Add(left);
        send = new Button { Content = "确认并发送", HorizontalAlignment = HorizontalAlignment.Right, Visibility = confirmSend ? Visibility.Visible : Visibility.Collapsed, IsEnabled = false };
        send.Click += (_, _) => DialogResult = true; footer.Children.Add(send);
        Content = root;
        chapters.SelectionChanged += async (_, _) => await LoadChapterAsync();
        Loaded += async (_, _) => await LoadAsync(); Closed += (_, _) => revision++;
    }
    private async Task LoadAsync()
    {
        body.Text = "正在读取书籍…";
        try
        {
            var details = await Task.Run(() => EpubPreview.Read(book.PreviewPath, book.Id));
            if (!IsVisible) return;
            heading.Text = details.Title;
            author.Text = details.Author;
            if (File.Exists(details.CoverPath)) cover.Source = ReadImage(details.CoverPath);
            summary.Text = book.Report is { } report ? $"检查 {report.ImagesFound} 张图片 · 补齐 {report.ManifestEntriesAdded} 项 · 打包校验通过\n"
                + (report.MissingImageFiles.Count > 0 ? $"提示：原书缺少 {report.MissingImageFiles.Count} 张图片，修复不能还原缺失文件。\n" : "")
                + (report.WebpImages.Count > 0 ? $"提示：含 {report.WebpImages.Count} 张 WebP 图片，部分 Kindle 设备可能无法显示。\n" : "")
                + "目录与正文为纯文字预览，Kindle 排版可能有所不同。" : "原书预览 · 尚未检查修复";
            chapters.ItemsSource = details.Chapters;
            if (details.Chapters.Count > 0) chapters.SelectedIndex = 0; else body.Text = "本书没有可预览的正文目录。";
            send.IsEnabled = book.Report is not null && File.Exists(book.Report.OutputPath);
        }
        catch (Exception ex)
        {
            if (IsVisible)
            {
                body.Text = "无法打开预览：" + ex.Message;
                send.IsEnabled = book.Report is not null && File.Exists(book.Report.OutputPath);
                send.Content = "仍然发送";
            }
        }
    }
    private async Task LoadChapterAsync()
    {
        if (chapters.SelectedItem is not PreviewChapter chapter) return;
        var current = ++revision; body.Text = "正在加载正文…";
        try { var text = await Task.Run(() => EpubPreview.ReadChapter(book.PreviewPath, chapter)); if (current == revision && IsVisible) { body.Text = text; body.ScrollToHome(); } }
        catch (Exception ex) { if (current == revision && IsVisible) body.Text = "此章节暂时无法预览：" + ex.Message; }
    }
    internal static BitmapSource? ReadImage(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 260; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
        }
        catch { return null; }
    }
}
