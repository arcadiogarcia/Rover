using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
using zRover.BackgroundManager.Sessions;

namespace zRover.BackgroundManager;

public partial class App : Application
{
    internal static IServiceProvider? Services { get; set; }

    private static App? _instance;
    private static DispatcherQueue? _dispatcherQueue;
    private Window? _window;
    private Window? _keepAlive; // Hidden window that keeps the dispatcher loop alive
    private SessionNotificationService? _notificationService;

    public App()
    {
        _instance = this;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        InitializeComponent();

        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
        }
        catch
        {
            // Notification registration requires COM activation entries in the
            // manifest (packaged) or a registered AUMID (unpackaged).
            // Silently skip — the app works without notifications.
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var registry = Services!.GetRequiredService<SessionRegistry>();
        _notificationService = new SessionNotificationService(registry);

        // Create a hidden window to keep the WinUI dispatcher loop alive.
        // Without this, Application.Start() exits when the user closes the
        // main window, making re-activation impossible.
        _keepAlive = new Window { Title = "" };
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_keepAlive);
        ShowWindow(hwnd, 0 /* SW_HIDE */);

        ShowMainWindow();
    }

    internal static void ShowMainWindow()
    {
        if (_instance == null) return;

        if (_instance._window == null)
        {
            _instance._window = new MainWindow();
            _instance._window.Closed += (_, _) => _instance._window = null;
        }
        _instance._window.Activate();
        BringToForeground(_instance._window);
    }

    private static void BringToForeground(Window window)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (IsIconic(hwnd))
            ShowWindow(hwnd, 9 /* SW_RESTORE */);

        // Windows blocks SetForegroundWindow unless the caller is the foreground
        // process. Temporarily attach to the foreground thread so the OS allows it.
        var foregroundHwnd = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foregroundHwnd, out _);
        var currentThread = GetCurrentThreadId();

        bool attached = false;
        if (foregroundThread != currentThread)
            attached = AttachThreadInput(currentThread, foregroundThread, true);

        SetForegroundWindow(hwnd);
        BringWindowToTop(hwnd);

        if (attached)
            AttachThreadInput(currentThread, foregroundThread, false);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    /// <summary>
    /// Called from the named-pipe listener (background thread) when a second
    /// instance is launched. Marshals to the UI thread.
    /// </summary>
    internal static void ActivateFromExternal()
    {
        _dispatcherQueue?.TryEnqueue(ShowMainWindow);
    }

    private static void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            ShowMainWindow();
            // Re-show the notification so it remains visible while sessions exist
            _instance?._notificationService?.Update();
        });
    }
}
