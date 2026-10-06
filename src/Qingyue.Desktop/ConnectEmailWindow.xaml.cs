using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;

namespace EpubKindleFix;

public partial class ConnectEmailWindow : Window
{
    private readonly AppSettings settings;
    private readonly MailProvider? provider;
    private readonly CancellationTokenSource closedCancellation = new();

    public ConnectEmailWindow(AppSettings settings)
    {
        InitializeComponent();
        this.settings = settings;
        provider = MailProviders.Find(settings.SenderEmail);
        EmailLabel.Text = settings.SenderEmail;
        ProviderLabel.Text = provider?.Name ?? "使用电脑上已登录的邮箱";
        InstructionsText.Text = provider?.Instructions ?? "请在电脑上的经典版 Outlook 中登录这个发件邮箱，再回来点击“重新检查已登录邮箱”。这个方式使用 Outlook 已有的登录，无需在本应用填写密码。新 Outlook 暂不支持这种连接方式。";
        if (provider is null)
        {
            CodePanel.Visibility = Visibility.Collapsed;
            OpenHelpButton.Visibility = Visibility.Collapsed;
            OpenProviderButton.Content = "查看 Outlook 登录方法";
            ConnectButton.Content = "重新检查已登录邮箱";
        }
        Closed += (_, _) => closedCancellation.Cancel();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ConnectionStatus.Text = "";
        if (provider is not null && string.IsNullOrWhiteSpace(AuthorizationCodeBox.Password))
        {
            ConnectionStatus.Text = "请先粘贴邮箱官方页面生成的授权码，再点击连接。";
            AuthorizationCodeBox.Focus();
            return;
        }
        ConnectButton.IsEnabled = false;
        ConnectingProgress.Visibility = Visibility.Visible;
        try
        {
            var draft = settings.ForAddresses(settings.SenderEmail, settings.KindleEmail, settings.AutoSend);
            if (provider is null)
            {
                if (!await OutlookDelivery.HasAccountAsync(settings.SenderEmail).WaitAsync(TimeSpan.FromSeconds(15)))
                    throw new InvalidOperationException("没有找到这个已登录账号。请先在经典版 Outlook 中登录同一个发件地址，再回来连接。");
                draft.DeliveryMode = "Outlook";
                draft.ProtectedPassword = "";
            }
            else
            {
                MailProviders.Apply(draft);
                draft.DeliveryMode = "Email";
                draft.SetPassword(Regex.Replace(AuthorizationCodeBox.Password, @"\s+", ""));
                await EmailDelivery.VerifyAsync(draft, closedCancellation.Token);
            }
            if (closedCancellation.IsCancellationRequested) return;
            settings.DeliveryMode = draft.DeliveryMode;
            settings.SmtpHost = draft.SmtpHost;
            settings.SmtpPort = draft.SmtpPort;
            settings.SmtpUsername = draft.SmtpUsername;
            settings.ProtectedPassword = draft.ProtectedPassword;
            settings.ConnectedSenderEmail = settings.SenderEmail;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            if (!closedCancellation.IsCancellationRequested)
                ConnectionStatus.Text = ex is InvalidOperationException ? ex.Message : EmailDelivery.FriendlyError(ex);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            ConnectingProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void OpenProvider_Click(object sender, RoutedEventArgs e) => OpenUrl(provider?.Website
        ?? "https://support.microsoft.com/en-us/outlook/getstarted/add-an-email-account-to-outlook-for-windows");
    private void OpenHelp_Click(object sender, RoutedEventArgs e) => OpenUrl(provider!.HelpUrl);
    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { ConnectionStatus.Text = "暂时无法打开浏览器，请稍后再试。"; }
    }
    private void Later_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
