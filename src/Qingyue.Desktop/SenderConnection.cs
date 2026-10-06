using System.Windows;

namespace EpubKindleFix;

public static class SenderConnection
{
    public static async Task<bool> ConnectAsync(Window owner, AppSettings settings)
    {
        if (settings.IsSenderConnected) return true;
        if (!MailProviders.TryAddress(settings.SenderEmail, out _)) return false;
        var found = false;
        try { found = await OutlookDelivery.HasAccountAsync(settings.SenderEmail).WaitAsync(TimeSpan.FromSeconds(15)); }
        catch { }
        if (!owner.IsVisible) return false;
        if (found)
        {
            settings.DeliveryMode = "Outlook";
            settings.ConnectedSenderEmail = settings.SenderEmail;
            settings.ProtectedPassword = "";
            return true;
        }
        var dialog = new ConnectEmailWindow(settings) { Owner = owner };
        return dialog.ShowDialog() == true;
    }
}
