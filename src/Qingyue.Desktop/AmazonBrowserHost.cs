using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace EpubKindleFix;

// A real, never-shown HWND keeps the upload viewport alive before any WPF
// window is displayed. The same controller is reparented for login/inspection.
// There is no Show/Hide flash and the existing AmazonBrowser-v1 profile is reused.
public sealed class AmazonBrowserHost : HwndHost
{
    private HwndSource? backgroundHost;
    private CoreWebView2Controller? controller;
    private IntPtr visibleHost;
    private bool disposed;
    public CoreWebView2? Core => controller?.CoreWebView2;

    public AmazonBrowserHost()
    {
        Focusable = true;
        IsVisibleChanged += (_, _) => { if (IsVisible) AttachToVisibleHost(); else Park(); };
    }

    public async Task InitializeAsync(CoreWebView2Environment environment)
    {
        if (disposed) throw new ObjectDisposedException(nameof(AmazonBrowserHost));
        if (controller is not null) return;
        backgroundHost ??= new HwndSource(new HwndSourceParameters("Qingyue background upload")
        {
            Width = 1068, Height = 640,
            WindowStyle = unchecked((int)0x86000000), // WS_POPUP | CLIPCHILDREN | CLIPSIBLINGS; no WS_VISIBLE.
            ExtendedWindowStyle = 0x08000080 // NOACTIVATE | TOOLWINDOW; no taskbar entry.
        });
        var created = await environment.CreateCoreWebView2ControllerAsync(backgroundHost.Handle);
        if (disposed) { created.Close(); throw new OperationCanceledException(); }
        controller = created;
        controller.Bounds = new System.Drawing.Rectangle(0, 0, 1068, 640);
        controller.IsVisible = true; // The ancestor HWND stays hidden; page layout remains usable.
        if (IsVisible) AttachToVisibleHost();
    }

    private void Park()
    {
        if (controller is null || backgroundHost is null || disposed) return;
        controller.ParentWindow = backgroundHost.Handle;
        controller.Bounds = new System.Drawing.Rectangle(0, 0, 1068, 640);
        controller.IsVisible = true;
    }

    private void AttachToVisibleHost()
    {
        if (controller is null || visibleHost == IntPtr.Zero || disposed) return;
        controller.ParentWindow = visibleHost;
        ResizeController();
        controller.IsVisible = true;
        controller.NotifyParentWindowPositionChanged();
    }

    private void ResizeController()
    {
        if (controller is null || visibleHost == IntPtr.Zero || !IsVisible) return;
        if (GetClientRect(visibleHost, out var rect) && rect.Right > 0 && rect.Bottom > 0)
            controller.Bounds = new System.Drawing.Rectangle(0, 0, rect.Right, rect.Bottom);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        visibleHost = CreateWindowEx(0, "STATIC", "", 0x56000000, 0, 0, 1068, 640,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (visibleHost == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (IsVisible) AttachToVisibleHost();
        return new HandleRef(this, visibleHost);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Park();
        DestroyWindow(hwnd.Handle);
        visibleHost = IntPtr.Zero;
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        ResizeController();
        controller?.NotifyParentWindowPositionChanged();
    }

    protected override bool TabIntoCore(TraversalRequest request)
    {
        if (controller is null || !IsVisible) return false;
        controller.MoveFocus(request.FocusNavigationDirection == FocusNavigationDirection.Previous
            ? CoreWebView2MoveFocusReason.Previous : CoreWebView2MoveFocusReason.Next);
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposed)
        {
            disposed = true;
            controller?.Close(); controller = null;
            backgroundHost?.Dispose(); backgroundHost = null;
        }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateWindowExW")]
    private static extern IntPtr CreateWindowEx(int extendedStyle, string className, string name, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
}
