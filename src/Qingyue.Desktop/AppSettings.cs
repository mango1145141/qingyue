using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EpubKindleFix;

public sealed class AppSettings
{
    public string KindleEmail { get; set; } = "";
    public string SenderEmail { get; set; } = "";
    public string SmtpHost { get; set; } = "smtp.gmail.com";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUsername { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";
    public bool AutoSend { get; set; } = true;
    public bool AutoRepair { get; set; } = true;
    public bool RememberHistory { get; set; } = true;
    public bool PreviewBeforeSend { get; set; }
    public string Theme { get; set; } = "浅色";
    public double TextScale { get; set; } = 1;
    public string Motion { get; set; } = "Q弹";
    public string ProtectedSyncCredential { get; set; } = "";
    public string SyncPendingJson { get; set; } = "{}";
    public string DeliveryMode { get; set; } = "Email";
    public string ConnectedSenderEmail { get; set; } = "";
    public string LibraryUrl { get; set; } = LibraryFiles.DefaultWebsiteUrl;
    public string RememberedLibraryUrl { get; set; } = "";
    public bool AmazonWebConnected { get; set; }
    public bool RememberedComicLogin { get; set; }
    public bool PreferWebUpload { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool ShowLibrarySearch => LibraryFiles.TryWebsite(LibraryUrl, out var active)
        && LibraryFiles.TryWebsite(RememberedLibraryUrl, out var remembered)
        && string.Equals(active!.GetLeftPart(UriPartial.Authority), remembered!.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSenderConnected => !string.IsNullOrWhiteSpace(SenderEmail)
        && (string.IsNullOrEmpty(ConnectedSenderEmail) || string.Equals(ConnectedSenderEmail, SenderEmail, StringComparison.OrdinalIgnoreCase))
        && (DeliveryMode == "Outlook"
            ? !string.IsNullOrEmpty(ConnectedSenderEmail)
            : !string.IsNullOrWhiteSpace(SmtpHost) && !string.IsNullOrWhiteSpace(SmtpUsername) && !string.IsNullOrEmpty(ReadPassword()));

    public AppSettings ForAddresses(string sender, string recipient, bool autoSend)
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;
        if (!string.Equals(SenderEmail, sender, StringComparison.OrdinalIgnoreCase))
        {
            copy.ProtectedPassword = "";
            copy.ConnectedSenderEmail = "";
            copy.DeliveryMode = "Email";
            copy.SmtpHost = "";
            copy.SmtpUsername = "";
        }
        copy.SenderEmail = sender;
        copy.KindleEmail = recipient;
        copy.AutoSend = autoSend;
        if (!copy.IsSenderConnected) MailProviders.Apply(copy);
        return copy;
    }

    public string ReadPassword()
    {
        if (string.IsNullOrEmpty(ProtectedPassword)) return "";
        try { return Dpapi.Unprotect(Convert.FromBase64String(ProtectedPassword)); }
        catch { return ""; }
    }

    public void SetPassword(string password) => ProtectedPassword = string.IsNullOrEmpty(password)
        ? "" : Convert.ToBase64String(Dpapi.Protect(password));
}

public static class AppSettingsStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KindleBookRepair", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            var migratedUrl = LibraryFiles.MigrateWebsiteUrl(settings.LibraryUrl);
            if (!string.Equals(settings.LibraryUrl, migratedUrl, StringComparison.Ordinal))
            {
                settings.LibraryUrl = migratedUrl;
                try { Save(settings); } catch { /* The corrected entry remains active for this launch. */ }
            }
            return settings;
        }
        catch { return new AppSettings(); }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var tempPath = SettingsPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tempPath, SettingsPath, overwrite: true);
    }
}

internal static class Dpapi
{
    private const int UiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static byte[] Protect(string value) => Transform(Encoding.UTF8.GetBytes(value), protect: true);
    public static string Unprotect(byte[] value) => Encoding.UTF8.GetString(Transform(value, protect: false));

    private static byte[] Transform(byte[] value, bool protect)
    {
        var inputPointer = Marshal.AllocHGlobal(value.Length);
        Marshal.Copy(value, 0, inputPointer, value.Length);
        var input = new DataBlob { Length = value.Length, Data = inputPointer };
        var output = new DataBlob();
        try
        {
            var ok = protect
                ? CryptProtectData(ref input, "KindleBookRepair", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            var bytes = new byte[output.Length];
            Marshal.Copy(output.Data, bytes, 0, output.Length);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(inputPointer);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }
}
