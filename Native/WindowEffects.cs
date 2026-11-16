using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Clipt.Native;

internal enum Backdrop { None = 1, Mica = 2, Acrylic = 3, Tabbed = 4 }

/// <summary>Win11 DWM integration: real system backdrop (acrylic/mica), rounded corners, dark frame.</summary>
internal static class WindowEffects
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_ROUND = 2;

    public static bool SupportsBackdrop => Environment.OSVersion.Version.Build >= 22621;

    /// <returns>True if a system backdrop is active (caller can keep its background translucent).</returns>
    public static bool Apply(Window window, Backdrop backdrop, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();

        int darkFlag = dark ? 1 : 0;
        Win32.DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));

        int corner = DWMWCP_ROUND;
        Win32.DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        if (!SupportsBackdrop || backdrop == Backdrop.None) return false;

        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
            target.BackgroundColor = Colors.Transparent;

        var margins = new Win32.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Win32.DwmExtendFrameIntoClientArea(hwnd, ref margins);

        int type = (int)backdrop;
        return Win32.DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int)) == 0;
    }

    /// <summary>Hide from Alt+Tab and the taskbar.</summary>
    public static void MakeToolWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        long ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
        ex = (ex | Win32.WS_EX_TOOLWINDOW) & ~Win32.WS_EX_APPWINDOW;
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        HideCaptionButtons(window);
    }

    /// <summary>Removes the system menu so DWM doesn't paint min/max/close buttons over our chrome-less UI.</summary>
    public static void HideCaptionButtons(Window window)
    {
        const long WS_SYSMENU = 0x80000, WS_MINIMIZEBOX = 0x20000, WS_MAXIMIZEBOX = 0x10000;
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        long style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE).ToInt64();
        style &= ~(WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_STYLE, new IntPtr(style));
    }
}
