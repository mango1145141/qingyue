using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EpubKindleFix;

public static class Appearance
{
    private sealed class FontInfo(double size, double maxHeight) { public double Size = size; public double MaxHeight = maxHeight; }
    private static readonly ConditionalWeakTable<DependencyObject, FontInfo> Fonts = new();
    public static string Motion { get; private set; } = "Q弹";
    public static bool Animate => Motion != "减少动效" && SystemParameters.ClientAreaAnimation;
    public static double Scale { get; private set; } = 1;
    public static void Configure(AppSettings settings)
    {
        Motion = settings.Motion;
        Scale = Math.Clamp(settings.TextScale, 1, 1.2);
        var app = Application.Current;
        if (app is null) return;
        var dark = settings.Theme == "深色" || settings.Theme == "跟随系统" && ReadSystemDark();
        Set("Canvas", dark ? "#17191D" : "#F5F5F7");
        Set("Ink", dark ? "#F2F2F5" : "#1D1D1F");
        Set("Muted", dark ? "#BDC3CD" : "#86868B");
        Set("Subtle", dark ? "#9CA7B4" : "#AEAEB2");
        Set("Line", dark ? "#3A414C" : "#E3E3E8");
        Set("Accent", dark ? "#9CB8D7" : "#53769A");
        Set("AccentSoft", dark ? "#303C4B" : "#EDF3F8");
        Set("Surface", dark ? "#23272F" : "#FFFFFF");
        Set("Field", dark ? "#2B303A" : "#FFFFFF");
        Set("SoftPanel", dark ? "#303540" : "#F2F2F7");
        Set("ButtonInk", dark ? "#182331" : "#FFFFFF");
        Set("DeepMuted", dark ? "#BDC3CD" : "#6E6E73");
        foreach (Window window in app.Windows) RefreshFonts(window);
        void Set(string key, string hex) => app.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }
    private static bool ReadSystemDark()
    {
        try { return Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0; }
        catch { return false; }
    }
    public static void ApplyFont(DependencyObject element)
    {
        if (element is TextBlock text)
        {
            // Icon fonts and the physical book texture keep their proportions.
            if (text.FontFamily.Source.Contains("Icons") || text.FontFamily.Source.Contains("MDL2")) return;
            var info = Fonts.GetValue(text, _ => new(text.FontSize, text.MaxHeight));
            text.SetCurrentValue(TextBlock.FontSizeProperty, info.Size * Scale);
            if (double.IsFinite(info.MaxHeight)) text.MaxHeight = info.MaxHeight * Scale;
        }
        else if (element is TextBox box)
        {
            var info = Fonts.GetValue(box, _ => new(box.FontSize, box.MaxHeight));
            box.SetCurrentValue(Control.FontSizeProperty, info.Size * Scale);
        }
    }
    public static void ApplyWindow(DependencyObject root) => RefreshFonts(root);
    private static void RefreshFonts(DependencyObject parent)
    {
        ApplyFont(parent);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) RefreshFonts(VisualTreeHelper.GetChild(parent, i));
    }
}
