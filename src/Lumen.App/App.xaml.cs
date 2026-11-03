using Lumen.Core.Diagnostics;
using Lumen.Core.Startup;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Lumen.App;

public partial class App : Application
{
    private static App? _current;
    private readonly bool _startedByStartupTask;
    private MainWindow? _window;

    public static AppHost Host { get; private set; } = null!;
    public static MainWindow? MainShell => _current?._window;

    public App(bool startedByStartupTask = false)
    {
        _current = this;
        _startedByStartupTask = startedByStartupTask;
        InitializeComponent();

        // Crash handling: log everything; keep running on UI exceptions we can survive.
        UnhandledException += OnXamlUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal("Unhandled exception (process terminating).", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception.", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var options = StartupOptions.Parse(Environment.GetCommandLineArgs().Skip(1));
        if (_startedByStartupTask) options = options with { AutoStart = true };

        Host = AppHost.Create(options);
        Log.Phase("services ready");

        _window = new MainWindow(Host);
        _window.Activate();
        Log.Phase("main window activated");
    }

    /// <summary>A second launch was redirected here: bring the shell back.</summary>
    internal static void OnRedirectedActivation(AppActivationArguments e)
    {
        Log.Info($"Activation redirected from another instance ({e.Kind}).");
        var window = MainShell;
        window?.DispatcherQueue.TryEnqueue(() => window.ReturnToShell());
    }

    private void OnXamlUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception.", e.Exception);
        // Most UI-thread exceptions (a failed image decode, a bad binding) are survivable. Marking
        // them handled keeps the console experience alive; the crash-loop guard still protects
        // against genuine startup crashes, which happen before this handler can help.
        e.Handled = true;
        _window?.ShowToast("Something went wrong - details were written to the log.");
    }
}
