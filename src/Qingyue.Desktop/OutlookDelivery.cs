using System.IO;
using System.Runtime.InteropServices;

namespace EpubKindleFix;

public static class OutlookDelivery
{
    public static Task<bool> HasAccountAsync(string sender) => RunSta(() =>
    {
        object? app = null, session = null, accounts = null, selected = null;
        try
        {
            var type = Type.GetTypeFromProgID("Outlook.Application");
            if (type is null) return false;
            app = Activator.CreateInstance(type);
            if (app is null) return false;
            session = ((dynamic)app).GetNamespace("MAPI");
            accounts = ((dynamic)session).Accounts;
            selected = FindAccount(accounts, sender);
            return selected is not null;
        }
        catch { return false; }
        finally { Release(selected); Release(accounts); Release(session); Release(app); }
    });

    public static Task<bool> SendAsync(string sender, string recipient, string filePath) => RunSta(() =>
    {
        object? app = null, session = null, accounts = null, selected = null, message = null, attachments = null, attachment = null;
        try
        {
            var type = Type.GetTypeFromProgID("Outlook.Application")
                ?? throw new InvalidOperationException("请在经典版 Outlook 中登录发件邮箱，再回来重新连接。");
            app = Activator.CreateInstance(type) ?? throw new InvalidOperationException("无法打开已连接的 Outlook。");
            session = ((dynamic)app).GetNamespace("MAPI");
            accounts = ((dynamic)session).Accounts;
            selected = FindAccount(accounts, sender)
                ?? throw new InvalidOperationException("已连接的发件邮箱在 Outlook 中不可用，请重新连接。");
            message = ((dynamic)app).CreateItem(0);
            ((dynamic)message).SendUsingAccount = (dynamic)selected;
            ((dynamic)message).To = recipient;
            ((dynamic)message).Subject = Path.GetFileNameWithoutExtension(filePath);
            ((dynamic)message).Body = "已修复的 EPUB 电子书，请发送到我的 Kindle。";
            attachments = ((dynamic)message).Attachments;
            attachment = ((dynamic)attachments).Add(Path.GetFullPath(filePath));
            ((dynamic)message).Send();
            return true;
        }
        finally
        {
            Release(attachment); Release(attachments); Release(message); Release(selected); Release(accounts); Release(session); Release(app);
        }
    });

    private static object? FindAccount(object accounts, string sender)
    {
        var count = (int)((dynamic)accounts).Count;
        for (var i = 1; i <= count; i++)
        {
            object account = ((dynamic)accounts).Item(i);
            var matches = false;
            try { matches = string.Equals((string)((dynamic)account).SmtpAddress, sender, StringComparison.OrdinalIgnoreCase); }
            finally { if (!matches) Release(account); }
            if (matches) return account;
        }
        return null;
    }

    private static Task<T> RunSta<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void Release(object? item)
    {
        if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
    }
}
