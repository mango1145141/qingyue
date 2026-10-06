using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EpubKindleFix;

public partial class SettingsWindow : Window
{
    public AppSettings? SavedSettings { get; private set; }
    private readonly Action<AppSettings> persistSettings;
    private AppSettings connectionSettings = new();
    private readonly ReadingProfile profile;
    private readonly Dictionary<string, CheckBox> favoriteGenres = new();
    private readonly Dictionary<string, CheckBox> excludedGenres = new();
    public bool RecommendationChanged { get; private set; }

    public SettingsWindow(AppSettings existing, Action<AppSettings>? persistSettings = null, ReadingProfile? profile = null)
    {
        InitializeComponent();
        this.persistSettings = persistSettings ?? AppSettingsStore.Save;
        connectionSettings = existing.ForAddresses(existing.SenderEmail, existing.KindleEmail, existing.AutoSend);
        KindleEmailBox.Text = existing.KindleEmail;
        SenderEmailBox.Text = existing.SenderEmail;
        AutoSendCheck.IsChecked = existing.AutoSend;
        AutoRepairCheck.IsChecked = existing.AutoRepair;
        RememberHistoryCheck.IsChecked = existing.RememberHistory;
        this.profile = profile ?? ReadingProfileStore.Load();
        PreviewBeforeSendCheck.IsChecked = existing.PreviewBeforeSend;
        ThemeBox.ItemsSource = new[] { "浅色", "深色", "跟随系统" }; ThemeBox.SelectedItem = existing.Theme;
        MotionBox.ItemsSource = new[] { "Q弹", "轻柔", "减少动效" }; MotionBox.SelectedItem = existing.Motion;
        TextScaleBox.ItemsSource = new[] { "标准", "大", "更大" }; TextScaleBox.SelectedIndex = existing.TextScale >= 1.19 ? 2 : existing.TextScale > 1.01 ? 1 : 0;
        DiscoveryModeBox.ItemsSource = new[] { "熟悉", "均衡", "探索" }; DiscoveryModeBox.SelectedItem = this.profile.DiscoveryMode;
        ChinesePriorityCheck.IsChecked = this.profile.ChinesePriority;
        foreach (var genre in ReadingCatalog.Books.SelectMany(b => b.Genres).Distinct())
        {
            AddGenre(FavoriteGenresPanel, favoriteGenres, genre, this.profile.FavoriteGenres.Contains(genre));
            AddGenre(ExcludedGenresPanel, excludedGenres, genre, this.profile.ExcludedGenres.Contains(genre));
        }
        RefreshConnection();
    }
    private void AddGenre(WrapPanel panel, Dictionary<string, CheckBox> boxes, string genre, bool selected)
    {
        var box = new CheckBox { Content = genre, IsChecked = selected, Margin = new Thickness(0,0,16,10), FontSize = 11, Style = (Style)FindResource("Switch") };
        boxes[genre] = box; panel.Children.Add(box);
    }

    private AppSettings Draft()
    {
        var draft = connectionSettings.ForAddresses(SenderEmailBox.Text.Trim(), KindleEmailBox.Text.Trim(), AutoSendCheck.IsChecked == true);
        draft.AutoRepair = AutoRepairCheck.IsChecked == true;
        draft.RememberHistory = RememberHistoryCheck.IsChecked == true;
        draft.PreviewBeforeSend = PreviewBeforeSendCheck.IsChecked == true;
        draft.Theme = ThemeBox.SelectedItem as string ?? "浅色";
        draft.Motion = MotionBox.SelectedItem as string ?? "Q弹";
        draft.TextScale = 1 + Math.Max(0, TextScaleBox.SelectedIndex) * 0.1;
        return draft;
    }

    private void SenderEmail_Changed(object sender, TextChangedEventArgs e)
    {
        if (SenderConnectionText is not null) RefreshConnection();
    }

    private void RefreshConnection()
    {
        var draft = Draft();
        var provider = MailProviders.Find(draft.SenderEmail);
        ProviderNotice.Text = provider is not null ? $"已识别 {provider.Name}，软件会自动处理发件设置。" : "填写你已被 Amazon 认可的发件邮箱地址。";
        SenderConnectionText.Text = draft.IsSenderConnected ? "发件邮箱已连接，可以自动发送。" : "首次使用，连接一次发件邮箱。";
        SenderConnectionText.Foreground = (Brush)FindResource(draft.IsSenderConnected ? "Success" : "Ink");
        ConnectSenderButton.Content = draft.IsSenderConnected ? "重新连接" : "连接发件邮箱";
    }

    private async void ConnectSender_Click(object sender, RoutedEventArgs e)
    {
        SettingsError.Visibility = Visibility.Collapsed;
        if (!MailProviders.TryAddress(SenderEmailBox.Text, out _))
        { ShowError("请先填写有效的发件邮箱地址。", SenderEmailBox); return; }
        var draft = Draft();
        draft.ProtectedPassword = "";
        draft.ConnectedSenderEmail = "";
        draft.DeliveryMode = "Email";
        ConnectSenderButton.IsEnabled = false;
        SaveSettingsButton.IsEnabled = false;
        SenderConnectionText.Text = "正在检查可用的发件邮箱…";
        try
        {
            if (await SenderConnection.ConnectAsync(this, draft)) connectionSettings = draft;
            if (IsVisible) RefreshConnection();
        }
        catch { if (IsVisible) ShowError("暂时无法连接发件邮箱，请稍后再试。"); }
        finally { ConnectSenderButton.IsEnabled = true; SaveSettingsButton.IsEnabled = true; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SettingsError.Visibility = Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(KindleEmailBox.Text) && !MailProviders.TryAddress(KindleEmailBox.Text, out _))
        { ShowError("Kindle 接收邮箱格式不正确，请检查后再保存。", KindleEmailBox); return; }
        if (!string.IsNullOrWhiteSpace(SenderEmailBox.Text) && !MailProviders.TryAddress(SenderEmailBox.Text, out _))
        { ShowError("发件邮箱格式不正确，请检查后再保存。", SenderEmailBox); return; }
        try
        {
            var favorites = favoriteGenres.Where(g => g.Value.IsChecked == true).Select(g => g.Key).ToList();
            var excluded = excludedGenres.Where(g => g.Value.IsChecked == true).Select(g => g.Key).ToList();
            if (excluded.Count == excludedGenres.Count) { ShowError("请至少保留一种推荐题材。"); return; }
            if (favorites.Intersect(excluded).Any()) { ShowError("同一种题材不能同时设为喜欢和排除。"); return; }
            var old = System.Text.Json.JsonSerializer.Serialize(profile);
            var oldFavorites = profile.FavoriteGenres; var oldExcluded = profile.ExcludedGenres;
            var oldMode = profile.DiscoveryMode; var oldChinese = profile.ChinesePriority;
            profile.FavoriteGenres = favorites; profile.ExcludedGenres = excluded;
            profile.DiscoveryMode = DiscoveryModeBox.SelectedItem as string ?? "均衡";
            profile.ChinesePriority = ChinesePriorityCheck.IsChecked == true;
            try { ReadingProfileStore.Save(profile); }
            catch { profile.FavoriteGenres = oldFavorites; profile.ExcludedGenres = oldExcluded; profile.DiscoveryMode = oldMode; profile.ChinesePriority = oldChinese; throw; }
            RecommendationChanged = old != System.Text.Json.JsonSerializer.Serialize(profile);
            var settings = Draft();
            try { persistSettings(settings); }
            catch
            {
                profile.FavoriteGenres = oldFavorites; profile.ExcludedGenres = oldExcluded;
                profile.DiscoveryMode = oldMode; profile.ChinesePriority = oldChinese;
                RecommendationChanged = false;
                ReadingProfileStore.Save(profile);
                throw;
            }
            SavedSettings = settings;
            DialogResult = true;
        }
        catch (Exception ex) { ShowError("设置未能保存：" + ex.Message); }
    }

    private void ShowError(string message, Control? field = null)
    {
        SettingsError.Text = message;
        SettingsError.Visibility = Visibility.Visible;
        field?.Focus();
        if (field is TextBox textBox) textBox.SelectAll();
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
