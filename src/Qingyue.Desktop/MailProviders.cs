using System.Net.Mail;

namespace EpubKindleFix;

public sealed record MailProvider(string Name, string Host, int Port, string Website, string HelpUrl, string Instructions);

public static class MailProviders
{
    public static MailProvider? Find(string email)
    {
        if (!TryAddress(email, out var address)) return null;
        return address!.Host.ToLowerInvariant() switch
        {
            "qq.com" or "foxmail.com" => new("QQ 邮箱", "smtp.qq.com", 465, "https://mail.qq.com/", "https://service.mail.qq.com/",
                "打开 QQ 邮箱的设置，找到允许第三方应用使用邮箱的选项，开启后按提示生成授权码。复制授权码，粘贴到下方即可。可在官方帮助中搜索“授权码”。"),
            "163.com" => new("163 邮箱", "smtp.163.com", 465, "https://mail.163.com/", "https://help.mail.163.com/",
                "打开 163 邮箱的设置，找到客户端授权或第三方应用的选项，开启后获取授权密码，再粘贴到下方。"),
            "gmail.com" or "googlemail.com" => new("Gmail", "smtp.gmail.com", 465, "https://myaccount.google.com/apppasswords", "https://support.google.com/accounts/answer/185833?hl=zh-Hans",
                "在 Google 官方页面创建应用专用密码。账号通常需要先开启两步验证。把生成的应用专用密码粘贴到下方，软件会记住这次连接。"),
            _ => null
        };
    }

    public static void Apply(AppSettings settings)
    {
        var provider = Find(settings.SenderEmail);
        if (provider is null) return;
        settings.SmtpHost = provider.Host;
        settings.SmtpPort = provider.Port;
        settings.SmtpUsername = settings.SenderEmail;
    }

    public static bool TryAddress(string value, out MailAddress? address)
    {
        address = null;
        try
        {
            var parsed = new MailAddress(value.Trim());
            if (parsed.Address != value.Trim()) return false;
            address = parsed;
            return true;
        }
        catch { return false; }
    }
}
