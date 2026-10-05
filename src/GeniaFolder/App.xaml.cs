using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace GeniaFolder;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\GeniaFolder.SingleInstance";
    private const string ActivateEventName = @"Local\GeniaFolder.ActivateExisting";

    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationWait;
    private Forms.NotifyIcon? _trayIcon;
    private bool _trayHintShown;

    internal bool ExitRequested { get; private set; }

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(false, MutexName);
        try
        {
            _ownsMutex = _instanceMutex.WaitOne(0, false);
        }
        catch (AbandonedMutexException)
        {
            // A previously force-killed instance cannot keep the app locked.
            _ownsMutex = true;
        }

        if (!_ownsMutex)
        {
            SignalRunningInstance();
            Shutdown();
            return;
        }

        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

        _activationEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            ActivateEventName);

        _activationWait = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, timedOut) =>
            {
                if (!timedOut)
                    Dispatcher.BeginInvoke(ShowMainWindow);
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        CreateTrayIcon();

        MainWindow = new MainWindow();
        MainWindow.Show();

        SessionEnding += (_, _) =>
        {
            ExitRequested = true;
        };
    }

    internal void ShowMainWindow()
    {
        if (MainWindow is null)
            return;

        if (!MainWindow.IsVisible)
            MainWindow.Show();

        if (MainWindow.WindowState == System.Windows.WindowState.Minimized)
            MainWindow.WindowState = System.Windows.WindowState.Normal;

        MainWindow.ShowInTaskbar = true;
        MainWindow.Activate();

        var handle = new WindowInteropHelper(MainWindow).Handle;
        if (handle != IntPtr.Zero)
            SetForegroundWindow(handle);
    }

    internal void NotifyHiddenToTray()
    {
        if (_trayHintShown || _trayIcon is null)
            return;

        _trayHintShown = true;
        _trayIcon.ShowBalloonTip(
            2500,
            "GeniaFolder",
            "GeniaFolder продолжает работать в области уведомлений. Для завершения используйте «Выход» в меню значка.",
            Forms.ToolTipIcon.Info);
    }

    internal void ExitApplication()
    {
        if (ExitRequested)
            return;

        ExitRequested = true;

        if (_trayIcon is not null)
            _trayIcon.Visible = false;

        MainWindow?.Close();
        Shutdown();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _activationWait?.Unregister(null);
        _activationWait = null;

        _activationEvent?.Dispose();
        _activationEvent = null;

        if (_ownsMutex && _instanceMutex is not null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        _instanceMutex?.Dispose();
        _instanceMutex = null;

        base.OnExit(e);
    }

    private void CreateTrayIcon()
    {
        var icon = ExtractApplicationIcon();

        var menu = new Forms.ContextMenuStrip();

        var openItem = new Forms.ToolStripMenuItem("Открыть GeniaFolder");
        openItem.Click += (_, _) => Dispatcher.BeginInvoke(ShowMainWindow);
        menu.Items.Add(openItem);

        var lockAllItem = new Forms.ToolStripMenuItem("Заблокировать всё (в 0.2)")
        {
            Enabled = false
        };
        menu.Items.Add(lockAllItem);

        menu.Items.Add(new Forms.ToolStripSeparator());

        var exitItem = new Forms.ToolStripMenuItem("Выход");
        exitItem.Click += (_, _) => Dispatcher.BeginInvoke(ExitApplication);
        menu.Items.Add(exitItem);

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "GeniaFolder",
            Icon = icon,
            ContextMenuStrip = menu,
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowMainWindow);
    }

    private static Icon ExtractApplicationIcon()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                var extracted = Icon.ExtractAssociatedIcon(processPath);
                if (extracted is not null)
                    return extracted;
            }
        }
        catch
        {
        }

        return SystemIcons.Application;
    }

    private static void SignalRunningInstance()
    {
        // The first process may still be creating its activation event.
        // Retry briefly, but never start a duplicate UI instance.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var activationEvent = EventWaitHandle.OpenExisting(ActivateEventName);
                activationEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(50);
            }
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
