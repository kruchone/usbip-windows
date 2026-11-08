using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;

namespace UsbIpClient;

public partial class App : Application
{
    private const string InstanceName = "UsbIp.SingleInstance";
    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private EventWaitHandle? _exitSignal;
    private TrayIcon? _tray;
    private AppController? _controller;

    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        var args = e.Args;

        // Elevated helper: run the driver installers downloaded by the normal (unelevated) instance.
        if (args.Length > 0 && args[0] == "--run-installers")
        {
            Shutdown(Prerequisites.RunInstallers(args.Skip(1)));
            return;
        }

        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            StopRunningInstance();
            SelfInstall.Uninstall();
            MessageBox.Show(
                "USB/IP was removed.\n\nThe usbip-win2 and usbipd-win drivers are still installed. Remove them from Settings → Apps → Installed apps if you no longer need them.",
                "USB/IP", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // First run of a downloaded exe (or a newer version): install per-user and relaunch from there.
        if (!SelfInstall.IsRunningInstalledCopy && !args.Contains("--portable", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                StopRunningInstance();
                SelfInstall.Install();
                var psi = new ProcessStartInfo(SelfInstall.InstalledExe) { UseShellExecute = false };
                foreach (var a in args) psi.ArgumentList.Add(a);
                Process.Start(psi);
                Shutdown();
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Couldn't install USB/IP: {ex.Message}\n\nRunning without installing.", "USB/IP",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        _mutex = new Mutex(true, InstanceName, out var first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Exit");
        if (!first)
        {
            // Already running: bring the existing window forward instead.
            _showSignal.Set();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        _controller = new AppController(Dispatcher);
        var window = new MainWindow(_controller);
        MainWindow = window;
        _tray = new TrayIcon(_controller, ShowMainWindow, ExitApp);

        var show = _showSignal;
        var exit = _exitSignal;
        new Thread(() =>
        {
            while (true)
            {
                var which = WaitHandle.WaitAny(new WaitHandle[] { show, exit });
                if (which == 0) Dispatcher.BeginInvoke(ShowMainWindow);
                else { Dispatcher.BeginInvoke(ExitApp); return; }
            }
        }) { IsBackground = true, Name = "instance-signals" }.Start();

        if (!args.Contains("--tray", StringComparer.OrdinalIgnoreCase)) window.Show();
        _controller.Start();

        // Diagnostics: --snapshot <file.png> [--tab share] renders the window to an image after it settles, then exits.
        var snap = Array.FindIndex(args, a => a.Equals("--snapshot", StringComparison.OrdinalIgnoreCase));
        if (snap >= 0 && snap + 1 < args.Length)
        {
            var path = args[snap + 1];
            if (args.Contains("share", StringComparer.OrdinalIgnoreCase)) window.SelectShareTab();
            var delay = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
            delay.Tick += (_, _) =>
            {
                delay.Stop();
                window.SaveSnapshot(path);
                ExitApp();
            };
            delay.Start();
        }
    }

    /** Asks a running instance to exit (e.g. before replacing its exe) and waits for it. */
    private static void StopRunningInstance()
    {
        if (!EventWaitHandle.TryOpenExisting(InstanceName + ".Exit", out var exit)) return;
        using (exit) exit.Set();
        try
        {
            using var m = Mutex.OpenExisting(InstanceName);
            if (m.WaitOne(TimeSpan.FromSeconds(10))) m.ReleaseMutex();
        }
        catch (WaitHandleCannotBeOpenedException) { }
        catch (AbandonedMutexException) { }
    }

    public void ShowMainWindow()
    {
        if (MainWindow == null) return;
        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    public void ShowTrayHint() =>
        _tray?.ShowHint("Still running", "USB/IP keeps your devices attached and shared from the notification area.");

    public void ExitApp()
    {
        IsExiting = true;
        _tray?.Dispose();
        _controller?.Stop();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { /* not owned */ }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
