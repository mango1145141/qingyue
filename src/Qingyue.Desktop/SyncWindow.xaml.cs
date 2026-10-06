using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;

namespace EpubKindleFix;

public partial class SyncWindow : Window
{
    private AppSettings settings;
    private readonly Action<AppSettings> persist;
    private bool working;
    private string pairLink = "";
    public SyncWindow(AppSettings settings, Action<AppSettings> persist)
    {
        InitializeComponent();
        this.settings = settings; this.persist = persist;
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 24);
        RefreshState();
    }
    private void RefreshState()
    {
        var connected = SettingsSync.Connected(settings);
        RefreshButton.IsEnabled = connected && !working;
        DisconnectButton.IsEnabled = connected && !working;
        GenerateButton.Content = pairLink.Length > 0 ? "重新生成二维码" : "生成手机配对二维码";
        if (!working) SyncStatus.Text = connected ? "已开启同步。手机扫描后会连接到同一份设置。" : "尚未配对，邮箱和偏好保存在本机。";
    }
    private async Task Run(Func<Task> action)
    {
        if (working) return;
        working = true;
        GenerateButton.IsEnabled = ConnectButton.IsEnabled = RefreshButton.IsEnabled = DisconnectButton.IsEnabled = DoneButton.IsEnabled = false;
        SyncStatus.Text = "正在连接…";
        try { await action(); }
        catch (Exception e) { SyncStatus.Text = e is SyncFailure or InvalidOperationException ? e.Message : "同步暂时不可用，设置已保留在本机，请稍后重试。"; }
        finally { working = false; GenerateButton.IsEnabled = ConnectButton.IsEnabled = DoneButton.IsEnabled = true; RefreshButton.IsEnabled = DisconnectButton.IsEnabled = SettingsSync.Connected(settings); }
    }
    private void Save(AppSettings next) { persist(next); settings = next; }
    private async void Generate_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        Save(await SettingsSync.CreateAsync(settings));
        Save(await SettingsSync.SynchronizeAsync(settings));
        var paired = await SettingsSync.PairAsync(settings);
        pairLink = SettingsSync.Website + "#pair=" + Uri.EscapeDataString(paired.PairCode);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(pairLink, QRCodeGenerator.ECCLevel.Q);
        using var png = new PngByteQRCode(data);
        using var stream = new MemoryStream(png.GetGraphic(8));
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        QrImage.Source = bitmap; QrImage.Visibility = CopyButton.Visibility = Visibility.Visible;
        ExpiryText.Text = "有效至 " + DateTimeOffset.FromUnixTimeMilliseconds(paired.ExpiresAt).ToLocalTime().ToString("HH:mm") + "，请用自己的手机扫描。";
        SyncStatus.Text = "二维码已生成。手机打开链接后即可配对。";
    });
    private async void Connect_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    { Save(await SettingsSync.ClaimAsync(settings, PairInput.Text)); PairInput.Clear(); SyncStatus.Text = "配对完成，邮箱和偏好已同步。"; });
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    { Save(await SettingsSync.SynchronizeAsync(settings)); SyncStatus.Text = "设置已同步。"; });
    private async void Disconnect_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    { Save(await SettingsSync.DisconnectAsync(settings)); pairLink = ""; QrImage.Source = null; QrImage.Visibility = CopyButton.Visibility = Visibility.Collapsed; ExpiryText.Text = ""; SyncStatus.Text = "此设备已断开，其他设备仍可同步。"; });
    private void Copy_Click(object sender, RoutedEventArgs e)
    { try { Clipboard.SetText(pairLink); SyncStatus.Text = "配对链接已复制，5 分钟内有效。"; } catch { SyncStatus.Text = "暂时无法复制，请扫描二维码。"; } }
    private void Done_Click(object sender, RoutedEventArgs e) { if (!working) Close(); }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { if (working) e.Cancel = true; base.OnClosing(e); }
}
