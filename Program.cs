using System.IO.Pipes;
using Avalonia;
using Avalonia.Controls;

namespace Clipt;

internal static class Program
{
    [STAThread] // the Windows clipboard and drag & drop need an STA UI thread
    public static int Main(string[] args)
    {
#if !WINDOWS
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("This build of Clipt runs on Linux. Use the Windows build on Windows.");
            return 1;
        }
        // Clipt talks to X from its own thread as well as Avalonia's; Xlib must be told before any other call.
        Platform.Linux.Xlib.XInitThreads();
#endif
        bool wantsSettings = args.Contains("--settings", StringComparer.OrdinalIgnoreCase);
        if (!SingleInstance.TryBecomePrimary())
        {
            // Already running: ask the existing instance to show itself (or its settings).
            SingleInstance.Signal(wantsSettings ? "settings" : "show");
            return 0;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions { WmClass = "Clipt" })
            .LogToTrace();
}

/// <summary>
/// One Clipt per user. The first instance holds a named mutex and listens on a named pipe (a Unix socket on
/// Linux, restricted to the current user) for "show" / "settings" requests from later launches.
/// </summary>
internal static class SingleInstance
{
    private static readonly string Id = $"Clipt.v1.{Environment.UserName}";
    private static Mutex? _mutex;

    public static event Action<string>? Received;

    public static bool TryBecomePrimary()
    {
        // On Linux a mutex without "Global\" is scoped to the Unix session, and apps started from autostart, the
        // app menu and a terminal can live in different sessions. The name carries the user, so make it global there.
        var name = (OperatingSystem.IsWindows() ? "" : @"Global\") + "Clipt.SingleInstance." + Id;
        _mutex = new Mutex(true, name, out bool created);
        if (created && IsAnotherInstanceListening()) created = false; // belt and braces
        if (!created)
        {
            _mutex.Dispose();
            _mutex = null;
        }
        return created;
    }

    private static bool IsAnotherInstanceListening()
    {
        try
        {
            using var probe = new NamedPipeClientStream(".", Id, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            probe.Connect(150);
            using var w = new StreamWriter(probe);
            w.WriteLine("ping");
            return true;
        }
        catch { return false; }
    }

    public static void Signal(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Id, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            using var w = new StreamWriter(client);
            w.WriteLine(command);
        }
        catch { /* the other instance is shutting down */ }
    }

    public static void Listen()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(Id, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync();
                    using var r = new StreamReader(server);
                    var line = (await r.ReadLineAsync())?.Trim();
                    if (line is "show" or "settings") Avalonia.Threading.Dispatcher.UIThread.Post(() => Received?.Invoke(line));
                }
                catch (Exception ex)
                {
                    App.Log(ex);
                    await Task.Delay(1000);
                }
            }
        });
    }
}
