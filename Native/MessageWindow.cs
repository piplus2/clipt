using System.Windows.Interop;

namespace Clipt.Native;

/// <summary>Invisible message-only window that receives clipboard and hotkey notifications.</summary>
internal sealed class MessageWindow : IDisposable
{
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private readonly HwndSource _source;

    public event Action<int, IntPtr, IntPtr>? Message;

    public IntPtr Handle => _source.Handle;

    public MessageWindow()
    {
        _source = new HwndSource(new HwndSourceParameters("CliptMessageWindow")
        {
            ParentWindow = HWND_MESSAGE,
            WindowStyle = 0,
        });
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is Win32.WM_CLIPBOARDUPDATE or Win32.WM_HOTKEY)
        {
            Message?.Invoke(msg, wParam, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose() => _source.Dispose();
}
