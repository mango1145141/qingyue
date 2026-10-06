using System.IO;
using MailKit.Security;
using MimeKit;

namespace EpubKindleFix;

public sealed class DeliveryFailure(Exception cause, bool safeToRetry, bool uncertain) : Exception(cause.Message, cause)
{
    public bool SafeToRetry { get; } = safeToRetry;
    public bool Uncertain { get; } = uncertain;
}

public static class EmailDelivery
{
    public static async Task VerifyAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        using var client = new MailKit.Net.Smtp.SmtpClient { Timeout = 30000 };
        await client.ConnectAsync(settings.SmtpHost, settings.SmtpPort, TlsMode(settings), cancellationToken);
        await client.AuthenticateAsync(settings.SmtpUsername, settings.ReadPassword(), cancellationToken);
        try { await client.DisconnectAsync(true, cancellationToken); } catch { }
    }

    public static async Task SendAsync(AppSettings settings, string filePath)
    {
        if (!settings.IsSenderConnected) throw new InvalidOperationException("请先连接发件邮箱，完成一次邮箱授权。");
        if (settings.DeliveryMode == "Outlook")
        {
            try { await OutlookDelivery.SendAsync(settings.SenderEmail, settings.KindleEmail, filePath); }
            catch (Exception ex) { throw new DeliveryFailure(ex, false, true); }
            return;
        }
        using var client = new MailKit.Net.Smtp.SmtpClient { Timeout = 60000 };
        try
        {
            await client.ConnectAsync(settings.SmtpHost, settings.SmtpPort, TlsMode(settings));
            await client.AuthenticateAsync(settings.SmtpUsername, settings.ReadPassword());
        }
        catch (Exception ex)
        {
            var retry = ex is System.Net.Sockets.SocketException or IOException or TimeoutException or OperationCanceledException;
            throw new DeliveryFailure(ex, retry, false);
        }
        using var message = CreateMessage(settings, filePath);
        try { await client.SendAsync(message); }
        catch (MailKit.Net.Smtp.SmtpCommandException ex)
        {
            throw new DeliveryFailure(ex, (int)ex.StatusCode is >= 400 and < 500, false);
        }
        catch (Exception ex) { throw new DeliveryFailure(ex, false, true); }
        // After acceptance, a disconnect failure must not turn into a retry and duplicate the book.
        try { await client.DisconnectAsync(true); } catch { }
    }

    public static MimeMessage CreateMessage(AppSettings settings, string filePath)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(settings.SenderEmail));
        message.To.Add(MailboxAddress.Parse(settings.KindleEmail));
        message.Subject = Path.GetFileNameWithoutExtension(filePath);
        var body = new BodyBuilder { TextBody = "已修复的 EPUB 电子书，请发送到我的 Kindle。" };
        body.Attachments.Add(filePath, new ContentType("application", "epub+zip"));
        message.Body = body.ToMessageBody();
        return message;
    }

    private static SecureSocketOptions TlsMode(AppSettings settings) => settings.SmtpPort == 465
        ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;

    public static string FriendlyError(Exception ex) => (ex is DeliveryFailure { InnerException: { } cause } ? cause : ex) switch
    {
        MailKit.Security.AuthenticationException => "邮箱没有接受这次授权。请按官方引导重新生成授权码后再试。",
        OperationCanceledException or TimeoutException => "连接超时，请检查网络后再试。",
        System.Net.Sockets.SocketException or IOException => "暂时无法连接发件邮箱，请检查网络后再试。",
        MailKit.Net.Smtp.SmtpCommandException => "邮箱服务拒绝了这次发送。请检查 Kindle 接收地址、附件大小和邮箱的发送限制。",
        SslHandshakeException => "无法建立安全连接。请检查电脑日期和网络环境后再试。",
        _ => "邮箱暂时无法完成连接或发送。请重新连接发件邮箱后再试。"
    };
}
