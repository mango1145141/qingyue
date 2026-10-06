namespace EpubKindleFix;

public partial class App : System.Windows.Application
{
    private Mutex? instanceMutex;
    private bool ownsInstance;
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        instanceMutex = new Mutex(true, @"Local\Qingyue.PersonalLibrary." + user, out ownsInstance);
        if (!ownsInstance)
        {
            var window = FindWindow(null, "轻阅 · " + typeof(MainWindow).Assembly.GetName().Version?.ToString(3));
            if (window != IntPtr.Zero) { if (IsIconic(window)) ShowWindowAsync(window,9); SetForegroundWindow(window); }
            else System.Windows.MessageBox.Show("轻阅已经在运行或正在启动，请稍候从任务栏打开。", "轻阅");
            Shutdown(); return;
        }
        System.Windows.EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase),
            System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new System.Windows.RoutedEventHandler((sender, args) =>
            {
                if (ReferenceEquals(sender, args.OriginalSource)) UiMotion.Pulse((System.Windows.Controls.Primitives.ButtonBase)sender);
            }));
        System.Windows.EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase),
            System.Windows.FrameworkElement.LoadedEvent, new System.Windows.RoutedEventHandler((sender, _) =>
                UiMotion.AttachButton((System.Windows.Controls.Primitives.ButtonBase)sender)));
        // Loaded class handlers do not reliably fire in this app — wire the
        // springs lazily from input events too.
        System.Windows.EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase),
            System.Windows.UIElement.MouseEnterEvent, new System.Windows.RoutedEventHandler((sender, _) =>
                UiMotion.ButtonHover((System.Windows.Controls.Primitives.ButtonBase)sender, true)));
        System.Windows.EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase),
            System.Windows.UIElement.MouseLeaveEvent, new System.Windows.RoutedEventHandler((sender, _) =>
                UiMotion.ButtonHover((System.Windows.Controls.Primitives.ButtonBase)sender, false)));
        System.Windows.EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase),
            System.Windows.UIElement.PreviewMouseLeftButtonDownEvent, new System.Windows.RoutedEventHandler((sender, _) =>
                UiMotion.ButtonPress((System.Windows.Controls.Primitives.ButtonBase)sender, true)));
        System.Windows.EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase),
            System.Windows.UIElement.PreviewMouseLeftButtonUpEvent, new System.Windows.RoutedEventHandler((sender, _) =>
                UiMotion.ButtonPress((System.Windows.Controls.Primitives.ButtonBase)sender, false)));
        System.Windows.EventManager.RegisterClassHandler(typeof(System.Windows.Controls.CheckBox),
            System.Windows.UIElement.MouseEnterEvent, new System.Windows.RoutedEventHandler((sender, _) =>
                UiMotion.SwitchHover((System.Windows.Controls.CheckBox)sender)));
        System.Windows.EventManager.RegisterClassHandler(typeof(System.Windows.Controls.CheckBox),
            System.Windows.FrameworkElement.LoadedEvent, new System.Windows.RoutedEventHandler((sender, _) =>
                UiMotion.AttachSwitch((System.Windows.Controls.CheckBox)sender)));
        System.Windows.EventManager.RegisterClassHandler(typeof(System.Windows.Controls.CheckBox),
            System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new System.Windows.RoutedEventHandler((sender, args) =>
            {
                if (ReferenceEquals(sender, args.OriginalSource)) UiMotion.SwitchChanged((System.Windows.Controls.CheckBox)sender);
            }));
        System.Windows.EventManager.RegisterClassHandler(typeof(System.Windows.Controls.CheckBox),
            System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new System.Windows.RoutedEventHandler((sender, args) =>
            {
                if (ReferenceEquals(sender, args.OriginalSource)) UiMotion.SwitchChanged((System.Windows.Controls.CheckBox)sender);
            }));
        System.Windows.EventManager.RegisterClassHandler(typeof(System.Windows.Controls.CheckBox),
            System.Windows.Controls.Primitives.ToggleButton.IndeterminateEvent, new System.Windows.RoutedEventHandler((sender, args) =>
            {
                if (ReferenceEquals(sender, args.OriginalSource)) UiMotion.SwitchChanged((System.Windows.Controls.CheckBox)sender);
            }));
        System.Windows.EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Expander),
            System.Windows.FrameworkElement.LoadedEvent, new System.Windows.RoutedEventHandler((sender, _) =>
                UiMotion.Expand((System.Windows.Controls.Expander)sender, false)));
        System.Windows.EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Expander),
            System.Windows.Controls.Expander.ExpandedEvent, new System.Windows.RoutedEventHandler((sender, args) =>
            {
                if (ReferenceEquals(sender, args.OriginalSource)) UiMotion.Expand((System.Windows.Controls.Expander)sender, true);
            }));
        System.Windows.EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Expander),
            System.Windows.Controls.Expander.CollapsedEvent, new System.Windows.RoutedEventHandler((sender, args) =>
            {
                if (ReferenceEquals(sender, args.OriginalSource)) UiMotion.Expand((System.Windows.Controls.Expander)sender, true);
            }));
        System.Windows.EventManager.RegisterClassHandler(typeof(System.Windows.Window),
            System.Windows.FrameworkElement.LoadedEvent, new System.Windows.RoutedEventHandler((sender, _) =>
            {
                // WebView2 has its own native rendering surface.
                if (sender is not (LibraryWindow or ServiceBrowserWindow) && sender is System.Windows.Window { Content: System.Windows.FrameworkElement content })
                { Appearance.ApplyWindow(content); UiMotion.Reveal(content, 10); }
            }));
        Appearance.Configure(AppSettingsStore.Load());
        System.Windows.EventManager.RegisterClassHandler(typeof(System.Windows.Controls.TextBlock),
            System.Windows.FrameworkElement.LoadedEvent, new System.Windows.RoutedEventHandler((sender, _) => Appearance.ApplyFont((System.Windows.DependencyObject)sender)));
        System.Windows.EventManager.RegisterClassHandler(typeof(System.Windows.Controls.TextBox),
            System.Windows.FrameworkElement.LoadedEvent, new System.Windows.RoutedEventHandler((sender, _) => Appearance.ApplyFont((System.Windows.DependencyObject)sender)));
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() => Appearance.Configure(AppSettingsStore.Load())));
        base.OnStartup(e);
    }
    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        if (ownsInstance) instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
    [System.Runtime.InteropServices.DllImport("user32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className,string title);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr window,int command);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
