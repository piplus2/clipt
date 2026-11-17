using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using static Clipt.Platform.Linux.Xlib;

namespace Clipt.Platform.Linux;

/// <summary>
/// Clipt's own connection to the X server, run on a dedicated thread (separate from Avalonia's).
/// It owns and serves the CLIPBOARD selection when Clipt copies something, requests selection data when Clipt
/// reads the clipboard (both with INCR for large data), watches ownership changes via XFixes, holds the global
/// hotkey grab, answers window queries and sends synthetic keystrokes via XTest.
/// Every Xlib call on this connection happens on the session thread.
/// </summary>
internal sealed unsafe partial class X11Session : IDisposable
{
    private readonly nint _dpy;
    private readonly nint _root;
    private readonly nint _win;
    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly int _wakeRead, _wakeWrite;
    private readonly int _fixesEventBase = -1;
    private readonly int _chunk;
    private volatile bool _stop;

    // Atoms
    private readonly nint _clipboard, _targets, _multiple, _timestamp, _incr, _utf8, _text, _textPlainUtf8, _textPlain,
        _html, _rtf, _png, _uriList, _gnomeFiles, _kdePasswordHint, _netActiveWindow, _netWmPid, _netWmName, _wmClass,
        _property;

    public bool HasXTest { get; }
    public bool HasXFixes => _fixesEventBase >= 0;

    /// <summary>Raised on the session thread when another client takes the CLIPBOARD selection.</summary>
    public event Action? ForeignClipboardChange;

    /// <summary>Raised on the session thread when the grabbed hotkey is pressed.</summary>
    public event Action? HotkeyPressed;

    public X11Session()
    {
        _dpy = XOpenDisplay(0);
        if (_dpy == 0) throw new InvalidOperationException("Can't connect to the X server. Clipt on Linux needs an X11 (or XWayland) session with DISPLAY set.");
        _root = XDefaultRootWindow(_dpy);
        _win = XCreateSimpleWindow(_dpy, _root, -10, -10, 1, 1, 0, 0, 0);
        XSelectInput(_dpy, _win, PropertyChangeMask);

        nint A(string name) => XInternAtom(_dpy, name, 0);
        _clipboard = A("CLIPBOARD");
        _targets = A("TARGETS");
        _multiple = A("MULTIPLE");
        _timestamp = A("TIMESTAMP");
        _incr = A("INCR");
        _utf8 = A("UTF8_STRING");
        _text = A("TEXT");
        _textPlainUtf8 = A("text/plain;charset=utf-8");
        _textPlain = A("text/plain");
        _html = A("text/html");
        _rtf = A("text/rtf");
        _png = A("image/png");
        _uriList = A("text/uri-list");
        _gnomeFiles = A("x-special/gnome-copied-files");
        _kdePasswordHint = A("x-kde-passwordManagerHint");
        _netActiveWindow = A("_NET_ACTIVE_WINDOW");
        _netWmPid = A("_NET_WM_PID");
        _netWmName = A("_NET_WM_NAME");
        _wmClass = A("WM_CLASS");
        _property = A("CLIPT_SELECTION");

        if (XFixesQueryExtension(_dpy, out int fixesEvent, out _) != 0)
        {
            _fixesEventBase = fixesEvent;
            XFixesSelectSelectionInput(_dpy, _root, _clipboard, XFixesSetSelectionOwnerNotifyMask);
        }
        HasXTest = XTestQueryExtension(_dpy, out _, out _, out _, out _) != 0;

        long maxRequest = XExtendedMaxRequestSize(_dpy);
        if (maxRequest == 0) maxRequest = XMaxRequestSize(_dpy);
        _chunk = (int)Math.Clamp(maxRequest * 4 - 1024, 4096, 256 * 1024);

        InstallErrorHandler(_dpy);

        var fds = stackalloc int[2];
        if (Libc.pipe(fds) != 0) throw new InvalidOperationException("pipe() failed");
        _wakeRead = fds[0];
        _wakeWrite = fds[1];
        Libc.fcntl(_wakeRead, Libc.F_SETFL, Libc.O_NONBLOCK);

        XFlush(_dpy);
        _thread = new Thread(Loop) { IsBackground = true, Name = "Clipt X11" };
        _thread.Start();
    }

    // ---------- thread & work queue ----------

    private void Loop()
    {
        var ev = stackalloc byte[XEventSize];
        var fds = stackalloc Libc.PollFd[2];
        var drain = stackalloc byte[64];
        int xfd = XConnectionNumber(_dpy);
        while (!_stop)
        {
            fds[0] = new Libc.PollFd { fd = xfd, events = Libc.POLLIN };
            fds[1] = new Libc.PollFd { fd = _wakeRead, events = Libc.POLLIN };
            Libc.poll(fds, 2, 100);
            while (Libc.read(_wakeRead, drain, 64) > 0) { }

            while (_work.TryDequeue(out var action))
            {
                try { action(); } catch (Exception ex) { App.Log(ex); }
            }
            while (XPending(_dpy) > 0)
            {
                XNextEvent(_dpy, ev);
                try { Handle(ev); } catch (Exception ex) { App.Log(ex); }
            }
            CheckTimeouts();
            XFlush(_dpy);
        }
        XDestroyWindow(_dpy, _win);
        XCloseDisplay(_dpy);
    }

    private void Wake()
    {
        byte b = 1;
        Libc.write(_wakeWrite, &b, 1);
    }

    /// <summary>Runs <paramref name="func"/> on the session thread.</summary>
    public Task<T> Run<T>(Func<T> func)
    {
        if (Thread.CurrentThread == _thread) return Task.FromResult(func());
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        Wake();
        return tcs.Task;
    }

    /// <summary>Runs on the session thread and waits (bounded) for the result. For quick queries only.</summary>
    public T Invoke<T>(Func<T> func, T fallback = default!)
    {
        var task = Run(func);
        return task.Wait(TimeSpan.FromSeconds(2)) ? task.Result : fallback;
    }

    private void Handle(byte* ev)
    {
        int type = Type(ev) & 0x7f;
        if (HasXFixes && type == _fixesEventBase)
        {
            if (Long(ev, FxSelection) == _clipboard)
            {
                var owner = Long(ev, FxOwner);
                if (owner != 0 && owner != _win)
                {
                    _foreignOwner = owner;
                    ForeignClipboardChange?.Invoke();
                }
            }
            return;
        }
        switch (type)
        {
            case SelectionRequest: OnSelectionRequest(ev); break;
            case SelectionNotify: OnSelectionNotify(ev); break;
            case SelectionClear:
                if (Long(ev, ScSelection) == _clipboard) { _served = null; _servedToken = 0; }
                break;
            case PropertyNotify: OnPropertyNotify(ev); break;
            case KeyPress:
                _lastUserTime = Long(ev, KeyTime);
                _lastUserTimeTicks = Environment.TickCount64;
                uint state = (uint)Int(ev, KeyState) & (ShiftMask | ControlMask | Mod1Mask | Mod4Mask);
                if (_hotkeyCode != 0 && Int(ev, KeyCode) == _hotkeyCode && state == _hotkeyMods)
                    HotkeyPressed?.Invoke();
                break;
        }
    }

    // ---------- error handling ----------

    // Xlib error handlers are process-wide. Ours records errors on Clipt's connection and hands everything else
    // (Avalonia's connection) to whatever handler was installed before.
    private static nint _ourDisplay;
    private static nint _previousHandler;
    private static ErrorHandler? _handler;
    private static volatile int _lastError;

    private static void InstallErrorHandler(nint dpy)
    {
        _ourDisplay = dpy;
        _handler = OnXError;
        _previousHandler = XSetErrorHandler(Marshal.GetFunctionPointerForDelegate(_handler));
    }

    private static int OnXError(nint dpy, nint errorEvent)
    {
        if (dpy == _ourDisplay)
        {
            _lastError = *((byte*)errorEvent + ErrCode);
            return 0;
        }
        return _previousHandler != 0
            ? ((delegate* unmanaged<nint, nint, int>)_previousHandler)(dpy, errorEvent)
            : 0;
    }

    // ---------- clipboard: serving (Clipt owns the selection) ----------

    private sealed record Served(Dictionary<nint, (byte[] data, nint type)> Formats);
    private Served? _served;
    private long _servedToken;
    private long _tokenCounter;

    private sealed class Transfer
    {
        public nint Requestor, Property, Type;
        public required byte[] Data;
        public int Offset;
        public long LastActivity;
    }
    private readonly List<Transfer> _transfers = [];

    /// <summary>Takes ownership of CLIPBOARD with <paramref name="content"/>. Returns a token, or 0 on failure.</summary>
    public long SetClipboard(ClipboardContent content) => Invoke(() =>
    {
        var formats = new Dictionary<nint, (byte[], nint)>();
        if (content.Files is { Length: > 0 } files)
        {
            var uris = files.Select(f => new Uri(f).AbsoluteUri).ToList();
            formats[_uriList] = (Encoding.UTF8.GetBytes(string.Join("\r\n", uris) + "\r\n"), _uriList);
            formats[_gnomeFiles] = (Encoding.UTF8.GetBytes("copy\n" + string.Join("\n", uris)), _gnomeFiles);
        }
        if (content.Text is { } text)
        {
            var utf8 = Encoding.UTF8.GetBytes(text);
            formats[_utf8] = (utf8, _utf8);
            formats[_textPlainUtf8] = (utf8, _textPlainUtf8);
            formats[_textPlain] = (utf8, _textPlain);
            formats[_text] = (utf8, _utf8);
            formats[XA_STRING] = (Encoding.Latin1.GetBytes(text), XA_STRING);
        }
        if (content.Html is { Length: > 0 } html) formats[_html] = (Encoding.UTF8.GetBytes(html), _html);
        if (content.Rtf is { Length: > 0 } rtf) formats[_rtf] = (Encoding.UTF8.GetBytes(rtf), _rtf);
        if (content.Image is { } png) formats[_png] = (png, _png);
        // KDE's convention, honoured by Klipper, CopyQ and others: don't keep this in clipboard history.
        if (content.IsPrivate) formats[_kdePasswordHint] = ("secret"u8.ToArray(), _kdePasswordHint);

        _transfers.Clear();
        _served = new Served(formats);
        XSetSelectionOwner(_dpy, _clipboard, _win, CurrentTime);
        if (XGetSelectionOwner(_dpy, _clipboard) != _win)
        {
            _served = null;
            return 0L;
        }
        _servedToken = ++_tokenCounter;
        return _servedToken;
    }, 0L);

    public bool OwnsClipboardWith(long token) => token != 0 && Invoke(() =>
        _servedToken == token && XGetSelectionOwner(_dpy, _clipboard) == _win);

    /// <summary>Drops the clipboard content if Clipt is still the one serving it.</summary>
    public void ClearClipboard() => Invoke(() =>
    {
        if (XGetSelectionOwner(_dpy, _clipboard) == _win)
            XSetSelectionOwner(_dpy, _clipboard, None, CurrentTime);
        _served = null;
        _servedToken = 0;
        _transfers.Clear();
        return true;
    });

    private void OnSelectionRequest(byte* ev)
    {
        nint requestor = Long(ev, SrRequestor), selection = Long(ev, SrSelection), target = Long(ev, SrTarget);
        nint property = Long(ev, SrProperty), time = Long(ev, SrTime);
        if (property == None) property = target; // obsolete clients
        bool ok = selection == _clipboard && _served is not null && Serve(requestor, target, property);

        var reply = stackalloc byte[XEventSize];
        new Span<byte>(reply, XEventSize).Clear();
        SetInt(reply, 0, SelectionNotify);
        SetLong(reply, SnRequestor, requestor);
        SetLong(reply, SnSelection, selection);
        SetLong(reply, SnTarget, target);
        SetLong(reply, SnProperty, ok ? property : None);
        SetLong(reply, SnTime, time);
        XSendEvent(_dpy, requestor, 0, 0, reply);
    }

    private bool Serve(nint requestor, nint target, nint property)
    {
        var formats = _served!.Formats;
        if (target == _targets)
        {
            var atoms = new List<nint> { _targets, _timestamp };
            atoms.AddRange(formats.Keys);
            var arr = atoms.ToArray();
            fixed (nint* p = arr) XChangeProperty(_dpy, requestor, property, XA_ATOM, 32, PropModeReplace, (byte*)p, arr.Length);
            return true;
        }
        if (target == _timestamp)
        {
            nint t = 0;
            XChangeProperty(_dpy, requestor, property, XA_INTEGER, 32, PropModeReplace, (byte*)&t, 1);
            return true;
        }
        if (!formats.TryGetValue(target, out var f)) return false; // MULTIPLE and unknown targets are refused

        if (f.data.Length <= _chunk)
        {
            fixed (byte* p = f.data) XChangeProperty(_dpy, requestor, property, f.type, 8, PropModeReplace, p, f.data.Length);
            return true;
        }

        // Large data: announce INCR, then send chunks each time the requestor deletes the property.
        XSelectInput(_dpy, requestor, PropertyChangeMask);
        nint size = f.data.Length;
        XChangeProperty(_dpy, requestor, property, _incr, 32, PropModeReplace, (byte*)&size, 1);
        _transfers.Add(new Transfer { Requestor = requestor, Property = property, Type = f.type, Data = f.data, LastActivity = Environment.TickCount64 });
        return true;
    }

    private void OnPropertyNotify(byte* ev)
    {
        nint window = Long(ev, OffWindow), atom = Long(ev, PnAtom);
        int state = Int(ev, PnState);

        if (window == _win && atom == _property && state == PropertyNewValue && _read is { Incr: true } read)
        {
            ContinueIncrRead(read);
            return;
        }

        if (state != PropertyDelete) return;
        var t = _transfers.Find(x => x.Requestor == window && x.Property == atom);
        if (t is null) return;
        int len = Math.Min(_chunk, t.Data.Length - t.Offset);
        fixed (byte* p = t.Data) XChangeProperty(_dpy, t.Requestor, t.Property, t.Type, 8, PropModeReplace, p + t.Offset, len);
        t.Offset += len;
        t.LastActivity = Environment.TickCount64;
        if (len == 0)
        {
            _transfers.Remove(t);
            if (!_transfers.Any(x => x.Requestor == window)) XSelectInput(_dpy, window, 0);
        }
    }

    // ---------- clipboard: reading (another app owns the selection) ----------

    private sealed class PendingRead
    {
        public required TaskCompletionSource<(byte[]? data, nint type)> Tcs;
        public bool Incr;
        public MemoryStream? Buffer;
        public long Deadline;
    }
    private PendingRead? _read;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private nint _foreignOwner;
    private nint _lastUserTime;
    private long _lastUserTimeTicks;

    /// <summary>The X time of the hotkey press if it just happened, else CurrentTime (a stale time would be refused).</summary>
    private nint RecentUserTime => Environment.TickCount64 - _lastUserTimeTicks < 3000 ? _lastUserTime : CurrentTime;
    private const int ReadTimeoutMs = 1500;
    private const long MaxReadBytes = 192L * 1024 * 1024;

    private void OnSelectionNotify(byte* ev)
    {
        if (Long(ev, SnRequestor) != _win || _read is not { } read) return;
        var property = Long(ev, SnProperty);
        if (property == None) { Complete(null, 0); return; }

        var data = GetProperty(_dpy, _win, property, out var type, out _, delete: true);
        if (type == _incr)
        {
            // Deleting the property (done above) tells the owner to start sending chunks.
            read.Incr = true;
            read.Buffer = new MemoryStream();
            read.Deadline = Environment.TickCount64 + ReadTimeoutMs;
            return;
        }
        Complete(data, type);
    }

    private void ContinueIncrRead(PendingRead read)
    {
        var chunk = GetProperty(_dpy, _win, _property, out var type, out _, delete: true);
        if (chunk is null) return;
        if (chunk.Length == 0) { Complete(read.Buffer!.ToArray(), type); return; }
        read.Buffer!.Write(chunk);
        read.Deadline = Environment.TickCount64 + ReadTimeoutMs;
        if (read.Buffer.Length > MaxReadBytes) Complete(null, 0);
    }

    private void Complete(byte[]? data, nint type)
    {
        var read = _read;
        _read = null;
        read?.Tcs.TrySetResult((data, type));
    }

    private void CheckTimeouts()
    {
        long now = Environment.TickCount64;
        if (_read is { } r && now > r.Deadline) Complete(null, 0);
        _transfers.RemoveAll(t => now - t.LastActivity > 5000);
    }

    internal static string[]? ParseFileUris(string list)
    {
        var lines = list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith('#') && l is not ("copy" or "cut"))
            .ToList();
        if (lines.Count == 0) return null;
        var paths = new List<string>();
        foreach (var l in lines)
        {
            // Only local files count as "files"; a list of web links stays text.
            if (!Uri.TryCreate(l, UriKind.Absolute, out var uri) || !uri.IsFile) return null;
            paths.Add(uri.LocalPath);
        }
        return [.. paths];
    }

    /// <summary>Firefox offers text/html as UTF-16; Chromium and most others as UTF-8.</summary>
    internal static string DecodeHtml(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2).TrimEnd('\0');
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2).TrimEnd('\0');
        if (b.Length >= 4 && b[1] == 0 && b[3] == 0) return Encoding.Unicode.GetString(b).TrimEnd('\0');
        return Encoding.UTF8.GetString(b).TrimEnd('\0');
    }

    // ---------- windows & processes ----------

    /// <summary>The window that last took the clipboard (often an invisible helper window of the app).</summary>
    public nint ClipboardOwner => Invoke(() => _foreignOwner != 0 ? _foreignOwner : XGetSelectionOwner(_dpy, _clipboard));

    public nint ActiveWindow() => Invoke(() => GetLongProperty(_dpy, _root, _netActiveWindow));

    public string? WindowTitle(nint w) => w == 0 ? null : Invoke(() =>
    {
        var name = GetProperty(_dpy, w, _netWmName, out _, out _);
        if (name is { Length: > 0 }) return Encoding.UTF8.GetString(name);
        var legacy = GetProperty(_dpy, w, XA_WM_NAME, out _, out _);
        return legacy is { Length: > 0 } ? Encoding.Latin1.GetString(legacy) : null;
    });

    /// <summary>WM_CLASS class name (e.g. "Gnome-terminal").</summary>
    public string? WindowClass(nint w) => w == 0 ? null : Invoke(() =>
    {
        var data = GetProperty(_dpy, w, _wmClass, out _, out _);
        if (data is null) return null;
        var parts = Encoding.Latin1.GetString(data).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[1] : parts.FirstOrDefault();
    });

    /// <summary>Process id behind a window: asks the X server (XRes) first, then the window's _NET_WM_PID.</summary>
    public int WindowPid(nint w) => w == 0 ? 0 : Invoke(() =>
    {
        int pid = XRes.ClientPid(_dpy, w);
        return pid > 0 ? pid : (int)GetLongProperty(_dpy, w, _netWmPid);
    });

    public (int x, int y)? Pointer() => Invoke<(int, int)?>(() =>
        XQueryPointer(_dpy, _root, out _, out _, out int x, out int y, out _, out _, out _) != 0 ? (x, y) : null, null);

    /// <summary>Centre of a window in root coordinates.</summary>
    public (int x, int y)? WindowCenter(nint w) => w == 0 ? null : Invoke<(int, int)?>(() =>
    {
        if (XGetGeometry(_dpy, w, out _, out _, out _, out uint width, out uint height, out _, out _) == 0) return null;
        if (XTranslateCoordinates(_dpy, w, _root, 0, 0, out int x, out int y, out _) == 0) return null;
        return (x + (int)width / 2, y + (int)height / 2);
    }, null);

    /// <summary>
    /// Asks the window manager to activate <paramref name="w"/>. Source "pager" plus the time of the user's
    /// hotkey press lets this through focus-stealing prevention (Mutter, KWin, Xfwm).
    /// </summary>
    public void Activate(nint w) => Invoke(() =>
    {
        var msg = stackalloc byte[XEventSize];
        new Span<byte>(msg, XEventSize).Clear();
        SetInt(msg, 0, ClientMessage);
        SetLong(msg, OffWindow, w);
        SetLong(msg, CmMessageType, _netActiveWindow);
        SetInt(msg, CmFormat, 32);
        SetLong(msg, CmData, 2);
        SetLong(msg, CmData + 8, RecentUserTime);
        SetLong(msg, CmData + 16, 0);
        XSendEvent(_dpy, _root, 0, SubstructureRedirectMask | SubstructureNotifyMask, msg);
        XFlush(_dpy);
        return true;
    });

    // ---------- keyboard ----------

    private int _hotkeyCode;
    private uint _hotkeyMods;

    /// <summary>Grabs the key combination on the root window. False if another app already holds it.</summary>
    public bool GrabHotkey(uint keysym, uint mods) => Invoke(() =>
    {
        UngrabHotkeyCore();
        int code = XKeysymToKeycode(_dpy, keysym);
        if (code == 0) return false;
        _lastError = 0;
        // Also grab with Caps Lock / Num Lock on, which X reports as extra modifiers.
        foreach (var extra in new[] { 0u, LockMask, Mod2Mask, LockMask | Mod2Mask })
            XGrabKey(_dpy, code, mods | extra, _root, 0, GrabModeAsync, GrabModeAsync);
        XSync(_dpy, 0);
        if (_lastError != 0)
        {
            foreach (var extra in new[] { 0u, LockMask, Mod2Mask, LockMask | Mod2Mask })
                XUngrabKey(_dpy, code, mods | extra, _root);
            XSync(_dpy, 0);
            return false;
        }
        _hotkeyCode = code;
        _hotkeyMods = mods;
        return true;
    });

    public void UngrabHotkey() => Invoke(() => { UngrabHotkeyCore(); return true; });

    private void UngrabHotkeyCore()
    {
        if (_hotkeyCode == 0) return;
        foreach (var extra in new[] { 0u, LockMask, Mod2Mask, LockMask | Mod2Mask })
            XUngrabKey(_dpy, _hotkeyCode, _hotkeyMods | extra, _root);
        XSync(_dpy, 0);
        _hotkeyCode = 0;
    }

    private const uint XK_Shift_L = 0xffe1, XK_Shift_R = 0xffe2, XK_Control_L = 0xffe3, XK_Alt_L = 0xffe9, XK_Alt_R = 0xffea,
        XK_Super_L = 0xffeb, XK_Super_R = 0xffec, XK_Meta_L = 0xffe7, XK_Meta_R = 0xffe8, XK_ISO_Level3_Shift = 0xfe03, XK_v = 0x76;

    /// <summary>True while Shift, Alt or Super is physically held (they'd turn Ctrl+V into another shortcut).</summary>
    public bool ModifiersHeld() => Invoke(() =>
    {
        var keys = stackalloc byte[32];
        XQueryKeymap(_dpy, keys);
        foreach (var sym in new[] { XK_Shift_L, XK_Shift_R, XK_Alt_L, XK_Alt_R, XK_Super_L, XK_Super_R, XK_Meta_L, XK_Meta_R, XK_ISO_Level3_Shift })
        {
            int code = XKeysymToKeycode(_dpy, sym);
            if (code != 0 && (keys[code / 8] & (1 << (code % 8))) != 0) return true;
        }
        return false;
    });

    /// <summary>Types Ctrl+V (or Ctrl+Shift+V for terminals) into the focused window.</summary>
    public bool SendPasteKeys(bool terminal) => HasXTest && Invoke(() =>
    {
        uint ctrl = XKeysymToKeycode(_dpy, XK_Control_L), shift = XKeysymToKeycode(_dpy, XK_Shift_L), v = XKeysymToKeycode(_dpy, XK_v);
        if (ctrl == 0 || v == 0) return false;
        XTestFakeKeyEvent(_dpy, ctrl, 1, 0);
        if (terminal) XTestFakeKeyEvent(_dpy, shift, 1, 0);
        XTestFakeKeyEvent(_dpy, v, 1, 0);
        XTestFakeKeyEvent(_dpy, v, 0, 0);
        if (terminal) XTestFakeKeyEvent(_dpy, shift, 0, 0);
        XTestFakeKeyEvent(_dpy, ctrl, 0, 0);
        XFlush(_dpy);
        return true;
    });

    public void Dispose()
    {
        if (_stop) return;
        try { UngrabHotkey(); } catch { }
        _stop = true;
        Wake();
        _thread.Join(TimeSpan.FromSeconds(1));
        Libc.close(_wakeRead);
        Libc.close(_wakeWrite);
    }
}

/// <summary>X-Resource extension: which process a window's X client belongs to.</summary>
internal static unsafe class XRes
{
    private const string Lib = "libXRes.so.1";
    private const uint XRES_CLIENT_ID_PID_MASK = 1 << 1;
    private static bool _unavailable;

    [StructLayout(LayoutKind.Sequential)] private struct ClientIdSpec { public nint client; public uint mask; }
    [StructLayout(LayoutKind.Sequential)] private struct ClientIdValue { public ClientIdSpec spec; public nint length; public nint value; }

    [DllImport(Lib)] private static extern int XResQueryClientIds(nint dpy, long numSpecs, ClientIdSpec* specs, out long numIds, out ClientIdValue* ids);
    [DllImport(Lib)] private static extern void XResClientIdsDestroy(long numIds, ClientIdValue* ids);

    public static int ClientPid(nint dpy, nint window)
    {
        if (_unavailable) return 0;
        try
        {
            var spec = new ClientIdSpec { client = window, mask = XRES_CLIENT_ID_PID_MASK };
            if (XResQueryClientIds(dpy, 1, &spec, out long n, out var ids) != 0) return 0;
            try
            {
                for (long i = 0; i < n; i++)
                    if (ids[i].spec.mask == XRES_CLIENT_ID_PID_MASK && ids[i].length >= 4 && ids[i].value != 0)
                        return *(int*)ids[i].value;
                return 0;
            }
            finally { XResClientIdsDestroy(n, ids); }
        }
        catch (DllNotFoundException) { _unavailable = true; return 0; }
        catch (EntryPointNotFoundException) { _unavailable = true; return 0; }
    }
}

internal static unsafe class Libc
{
    public const short POLLIN = 1;
    public const int F_SETFL = 4, O_NONBLOCK = 0x800;

    [StructLayout(LayoutKind.Sequential)] public struct PollFd { public int fd; public short events; public short revents; }

    [DllImport("libc", SetLastError = true)] public static extern int pipe(int* fds);
    [DllImport("libc", SetLastError = true)] public static extern int poll(PollFd* fds, nuint nfds, int timeout);
    [DllImport("libc", SetLastError = true)] public static extern nint read(int fd, byte* buf, nuint count);
    [DllImport("libc", SetLastError = true)] public static extern nint write(int fd, byte* buf, nuint count);
    [DllImport("libc", SetLastError = true)] public static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] public static extern int fcntl(int fd, int cmd, int arg);
    [DllImport("libc")] public static extern int malloc_trim(nuint pad);
}
