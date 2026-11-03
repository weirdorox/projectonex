using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Lumen.App;

/// <summary>
/// Custom entry point (DISABLE_XAML_GENERATED_MAIN) that enforces a single shell instance.
/// Launching Lumen again - from the Start menu, a hotkey launcher or the autostart entry - redirects
/// to the running instance, which comes back to the foreground instead of opening a second shell.
/// </summary>
public static class Program
{
    private const string InstanceKey = "Lumen.HandheldShell";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!main.IsCurrent)
        {
            // Redirect on a worker thread so the STA main thread is not blocked inside a COM call.
            Task.Run(() => main.RedirectActivationToAsync(activation).AsTask()).Wait(TimeSpan.FromSeconds(5));
            return 0;
        }

        main.Activated += (_, e) => App.OnRedirectedActivation(e);

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(activation.Kind == ExtendedActivationKind.StartupTask);
        });
        return 0;
    }
}
