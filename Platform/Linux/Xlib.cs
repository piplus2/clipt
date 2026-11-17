using System.Runtime.InteropServices;

namespace Clipt.Platform.Linux;

/// <summary>
/// The small part of Xlib, XFixes and XTest Clipt needs. Event structs are read from a raw XEvent buffer by
/// offset (LP64 layout: longs and pointers are 8 bytes, ints 4) instead of declaring the whole union.
/// </summary>
internal static unsafe partial class Xlib
{
    private const string X11 = "libX11.so.6";
    private const string XFixes = "libXfixes.so.3";
    private const string XTst = "libXtst.so.6";

    public const int XEventSize = 192; // sizeof(XEvent) on LP64: long pad[24]

    // Event types
    public const int KeyPress = 2, KeyRelease = 3, DestroyNotify = 17, PropertyNotify = 28,
        SelectionClear = 29, SelectionRequest = 30, SelectionNotify = 31, ClientMessage = 33;

    // Event masks
    public const long KeyPressMask = 1L << 0, PropertyChangeMask = 1L << 22,
        SubstructureNotifyMask = 1L << 19, SubstructureRedirectMask = 1L << 20;

    // Modifier masks
    public const uint ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod1Mask = 8, Mod2Mask = 16, Mod4Mask = 64;

    public const int PropModeReplace = 0, PropertyNewValue = 0, PropertyDelete = 1;
    public const int GrabModeAsync = 1;
    public const nint AnyPropertyType = 0, None = 0, CurrentTime = 0;
    public const nint XA_ATOM = 4, XA_CARDINAL = 6, XA_INTEGER = 19, XA_STRING = 31, XA_WINDOW = 33, XA_WM_NAME = 39;
    public const int Success = 0, BadAccess = 10;
    public const long XFixesSetSelectionOwnerNotifyMask = 1;

    [DllImport(X11)] public static extern int XInitThreads();
    [DllImport(X11)] public static extern nint XOpenDisplay(nint name);
    [DllImport(X11)] public static extern int XCloseDisplay(nint dpy);
    [DllImport(X11)] public static extern int XConnectionNumber(nint dpy);
    [DllImport(X11)] public static extern nint XDefaultRootWindow(nint dpy);
    [DllImport(X11)] public static extern nint XCreateSimpleWindow(nint dpy, nint parent, int x, int y, uint w, uint h, uint border, nuint borderPixel, nuint background);
    [DllImport(X11)] public static extern int XDestroyWindow(nint dpy, nint w);
    [DllImport(X11)] public static extern int XSelectInput(nint dpy, nint w, long mask);
    [DllImport(X11)] public static extern int XPending(nint dpy);
    [DllImport(X11)] public static extern int XNextEvent(nint dpy, byte* ev);
    [DllImport(X11)] public static extern int XFlush(nint dpy);
    [DllImport(X11)] public static extern int XSync(nint dpy, int discard);
    [DllImport(X11)] public static extern int XFree(nint data);
    [DllImport(X11)] public static extern int XSendEvent(nint dpy, nint w, int propagate, long mask, byte* ev);

    [DllImport(X11, CharSet = CharSet.Ansi)] public static extern nint XInternAtom(nint dpy, string name, int onlyIfExists);
    [DllImport(X11)] public static extern nint XGetAtomName(nint dpy, nint atom);

    [DllImport(X11)] public static extern int XSetSelectionOwner(nint dpy, nint selection, nint owner, nint time);
    [DllImport(X11)] public static extern nint XGetSelectionOwner(nint dpy, nint selection);
    [DllImport(X11)] public static extern int XConvertSelection(nint dpy, nint selection, nint target, nint property, nint requestor, nint time);

    [DllImport(X11)] public static extern int XChangeProperty(nint dpy, nint w, nint property, nint type, int format, int mode, byte* data, int nelements);
    [DllImport(X11)] public static extern int XDeleteProperty(nint dpy, nint w, nint property);
    [DllImport(X11)]
    public static extern int XGetWindowProperty(nint dpy, nint w, nint property, long offset, long length, int delete, nint reqType,
        out nint actualType, out int actualFormat, out nuint nitems, out nuint bytesAfter, out nint prop);
    [DllImport(X11)] public static extern long XMaxRequestSize(nint dpy);
    [DllImport(X11)] public static extern long XExtendedMaxRequestSize(nint dpy);

    [DllImport(X11)] public static extern byte XKeysymToKeycode(nint dpy, nuint keysym);
    [DllImport(X11)] public static extern int XGrabKey(nint dpy, int keycode, uint modifiers, nint grabWindow, int ownerEvents, int pointerMode, int keyboardMode);
    [DllImport(X11)] public static extern int XUngrabKey(nint dpy, int keycode, uint modifiers, nint grabWindow);
    [DllImport(X11)] public static extern int XQueryKeymap(nint dpy, byte* keys32);
    [DllImport(X11)]
    public static extern int XQueryPointer(nint dpy, nint w, out nint root, out nint child, out int rootX, out int rootY,
        out int winX, out int winY, out uint mask);
    [DllImport(X11)]
    public static extern int XGetGeometry(nint dpy, nint d, out nint root, out int x, out int y, out uint w, out uint h,
        out uint border, out uint depth);
    [DllImport(X11)]
    public static extern int XTranslateCoordinates(nint dpy, nint src, nint dest, int srcX, int srcY, out int destX, out int destY, out nint child);

    public delegate int ErrorHandler(nint dpy, nint errorEvent);
    [DllImport(X11)] public static extern nint XSetErrorHandler(nint handler);

    [DllImport(XFixes)] public static extern int XFixesQueryExtension(nint dpy, out int eventBase, out int errorBase);
    [DllImport(XFixes)] public static extern void XFixesSelectSelectionInput(nint dpy, nint w, nint selection, long mask);

    [DllImport(XTst)] public static extern int XTestQueryExtension(nint dpy, out int eventBase, out int errorBase, out int major, out int minor);
    [DllImport(XTst)] public static extern int XTestFakeKeyEvent(nint dpy, uint keycode, int isPress, nuint delay);

    // ---------- event field access (LP64 offsets) ----------

    public static int Type(byte* ev) => *(int*)ev;
    public static nint Long(byte* ev, int offset) => *(nint*)(ev + offset);
    public static int Int(byte* ev, int offset) => *(int*)(ev + offset);
    public static void SetLong(byte* ev, int offset, nint value) => *(nint*)(ev + offset) = value;
    public static void SetInt(byte* ev, int offset, int value) => *(int*)(ev + offset) = value;

    // XAnyEvent: type 0, serial 8, send_event 16, display 24, window 32
    public const int OffWindow = 32;
    // XSelectionRequestEvent: owner 32, requestor 40, selection 48, target 56, property 64, time 72
    public const int SrRequestor = 40, SrSelection = 48, SrTarget = 56, SrProperty = 64, SrTime = 72;
    // XSelectionEvent: requestor 32, selection 40, target 48, property 56, time 64
    public const int SnRequestor = 32, SnSelection = 40, SnTarget = 48, SnProperty = 56, SnTime = 64;
    // XSelectionClearEvent: window 32, selection 40, time 48
    public const int ScSelection = 40;
    // XPropertyEvent: window 32, atom 40, time 48, state 56
    public const int PnAtom = 40, PnState = 56;
    // XKeyEvent: window 32, root 40, subwindow 48, time 56, x 64, y 68, x_root 72, y_root 76, state 80, keycode 84
    public const int KeyTime = 56, KeyState = 80, KeyCode = 84;
    // XFixesSelectionNotifyEvent: window 32, subtype 40, owner 48, selection 56, timestamp 64, selection_timestamp 72
    public const int FxOwner = 48, FxSelection = 56;
    // XClientMessageEvent: window 32, message_type 40, format 48, data.l[5] 56
    public const int CmMessageType = 40, CmFormat = 48, CmData = 56;
    // XErrorEvent: type 0, display 8, resourceid 16, serial 24, error_code 32, request_code 33
    public const int ErrDisplay = 8, ErrCode = 32;

    // ---------- helpers ----------

    public static string? AtomName(nint dpy, nint atom)
    {
        if (atom == 0) return null;
        var p = XGetAtomName(dpy, atom);
        if (p == 0) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { XFree(p); }
    }

    /// <summary>Reads a whole window property (any size).</summary>
    public static byte[]? GetProperty(nint dpy, nint w, nint property, out nint type, out int format, bool delete = false, nint reqType = AnyPropertyType)
    {
        type = 0;
        format = 0;
        using var ms = new MemoryStream();
        long offset = 0;
        while (true)
        {
            if (XGetWindowProperty(dpy, w, property, offset, 1 << 20, 0, reqType, out type, out format,
                    out var nitems, out var after, out var prop) != Success)
                return null;
            try
            {
                if (type == None) return null;
                int unit = format switch { 8 => 1, 16 => sizeof(short), 32 => sizeof(nint), _ => 1 };
                // Format-32 data arrives as C longs (8 bytes each on LP64); keep that layout for the caller.
                long bytes = (long)nitems * unit;
                if (prop != 0 && bytes > 0) ms.Write(new ReadOnlySpan<byte>((void*)prop, checked((int)bytes)));
                // offset is in 32-bit units of the server-side data
                offset += format switch { 8 => (long)nitems / 4, 16 => (long)nitems / 2, _ => (long)nitems };
                if (after == 0) break;
                if (format == 8 && (long)nitems % 4 != 0) break; // unaligned tail: server already sent everything
            }
            finally { if (prop != 0) XFree(prop); }
        }
        if (delete) XDeleteProperty(dpy, w, property);
        return ms.ToArray();
    }

    /// <summary>First value of a format-32 property (a window, atom or cardinal).</summary>
    public static nint GetLongProperty(nint dpy, nint w, nint property)
    {
        var data = GetProperty(dpy, w, property, out _, out int format);
        return data is { Length: >= 8 } && format == 32 ? BitConverter.ToInt64(data) is var v ? (nint)v : 0 : 0;
    }

    public static nint[] Longs(byte[] data) =>
        Enumerable.Range(0, data.Length / 8).Select(i => (nint)BitConverter.ToInt64(data, i * 8)).ToArray();
}
