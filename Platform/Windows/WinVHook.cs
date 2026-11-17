using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace Clipt.Platform.Windows;

/// <summary>
/// Optional low-level keyboard hook that takes over Win+V (the built-in clipboard history shortcut).
/// Win+key combos owned by the shell cannot be claimed with RegisterHotKey, so we intercept them here.
/// </summary>
internal sealed class WinVHook : IDisposable
{
    // An unassigned virtual key: injecting it stops the Start menu from opening when Win is released.
    private const int VK_DUMMY = 0xE8;

    private readonly Action _onTrigger;
    private readonly Win32.LowLevelKeyboardProc _proc; // keep a reference so the GC doesn't collect it
    private IntPtr _hook;
    private bool _swallowKeyUp;

    public WinVHook(Action onTrigger)
    {
        _onTrigger = onTrigger;
        _proc = HookProc;
    }

    public bool Enabled
    {
        get => _hook != IntPtr.Zero;
        set
        {
            if (value == Enabled) return;
            if (value) _hook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _proc, Win32.GetModuleHandle(null), 0);
            else { Win32.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            int msg = wParam.ToInt32();
            bool injected = (info.flags & Win32.LLKHF_INJECTED) != 0;

            if (!injected && info.vkCode == Win32.VK_V)
            {
                if (msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN &&
                    (Win32.IsKeyDown(Win32.VK_LWIN) || Win32.IsKeyDown(Win32.VK_RWIN)) &&
                    !Win32.IsKeyDown(Win32.VK_CONTROL) && !Win32.IsKeyDown(Win32.VK_MENU) && !Win32.IsKeyDown(Win32.VK_SHIFT))
                {
                    Win32.SendKeys(Win32.Key(VK_DUMMY, false), Win32.Key(VK_DUMMY, true));
                    _swallowKeyUp = true;
                    Dispatcher.UIThread.Post(_onTrigger);
                    return new IntPtr(1);
                }
                if (msg is Win32.WM_KEYUP or Win32.WM_SYSKEYUP && _swallowKeyUp)
                {
                    _swallowKeyUp = false;
                    return new IntPtr(1);
                }
            }
        }
        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Enabled = false;
}
