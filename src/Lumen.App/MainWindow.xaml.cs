using Lumen.App.Controls;
using Lumen.App.Input;
using Lumen.App.Services;
using Lumen.App.Views;
using Lumen.Core.Diagnostics;
using Lumen.Core.Hardware;
using Lumen.Core.Input;
using Lumen.Core.Power;
using Lumen.Platform.Windows.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Lumen.App;

/// <summary>
/// The console shell window: fullscreen host for the pages, top bar, button legend and toasts.
/// Owns controller routing, desktop switching, the quick-access overlay and power handling.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly Type[] TabPages = [typeof(HomePage), typeof(LibraryPage), typeof(SettingsPage)];

    private readonly AppHost _host;
    private readonly IntPtr _hwnd;
    private readonly GamepadNavigator _navigator;
    private readonly WindowMessageHook _hook;
    private readonly DispatcherQueueTimer _clockTimer;
    private readonly DispatcherQueueTimer _batteryTimer;
    private readonly DispatcherQueueTimer _toastTimer;
    private readonly DispatcherQueueTimer _stableTimer;
    private QuickAccessWindow? _overlay;
    private int _tab = -1;
    private bool _wantFullscreen;
    private bool _closing;

    public MainWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ArtworkService.Initialize(host.Paths, DispatcherQueue);
        Library = new LibraryStore(host, DispatcherQueue);

        // ---- window chrome ----
        Title = "Lumen";
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Lumen.ico")); } catch { /* cosmetic */ }
        AppWindow.Closing += (_, _) => _closing = true;
        Closed += OnClosed;
        Activated += OnActivated;

        _wantFullscreen = !host.IsSafeMode && !host.Options.Windowed && host.Settings.Fullscreen;
        ApplyPresenter();
        SafeModeBadge.Visibility = host.IsSafeMode ? Visibility.Visible : Visibility.Collapsed;

        // ---- native hooks: global hotkey, session end, display change ----
        _hook = new WindowMessageHook(_hwnd);
        _hook.RegisterToggleHotkey();
        _hook.HotkeyPressed += _ => ReturnToShell();
        _hook.SessionEnding += () => _host.Guard.MarkCleanExit();
        _hook.DisplayChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (_wantFullscreen && DesktopSwitcher.IsForeground(_hwnd)) ApplyPresenter();
        });

        // ---- controller ----
        _navigator = new GamepadNavigator(DispatcherQueue, host.Gamepad, IsShellForeground);
        _navigator.ActionReceived += OnAction;
        _navigator.QuickAccessRequested += ToggleQuickAccess;
        _navigator.EmergencyDesktopRequested += EmergencyDesktop;
        host.Gamepad.ConnectionChanged += (_, e) =>
        {
            UpdateControllerIcon();
            ShowToast(e.Connected ? "Controller connected" : "Controller disconnected");
        };
        _navigator.Start();

        // ---- power: sleep/resume and display on/off ----
        host.Power.Suspending += (_, _) => DispatcherQueue.TryEnqueue(() => _navigator.Pause());
        host.Power.Resumed += (_, _) => DispatcherQueue.TryEnqueue(OnResumed);
        host.Power.DisplayStateChanged += (_, on) => DispatcherQueue.TryEnqueue(() =>
        {
            if (on) OnResumed();
            else _navigator.Pause();
        });
        host.Power.Start();

        // ---- timers ----
        _clockTimer = CreateTimer(TimeSpan.FromSeconds(15), UpdateClock);
        _batteryTimer = CreateTimer(TimeSpan.FromSeconds(30), UpdateBattery);
        _toastTimer = CreateTimer(TimeSpan.FromSeconds(3), () => { Toast.Visibility = Visibility.Collapsed; _toastTimer!.Stop(); });
        _stableTimer = CreateTimer(_host.Guard.StableAfter, () => { _host.Guard.MarkStable(); _stableTimer!.Stop(); });
        _clockTimer.Start();
        _batteryTimer.Start();
        _stableTimer.Start();
        UpdateClock();
        UpdateBattery();
        UpdateControllerIcon();

        // ---- content ----
        if (host.IsSafeMode) NavigateToSafeMode();
        else NavigateTab(0);

        _ = StartLibraryAsync();
        _ = WarmUpHardwareAsync();
    }

    public LibraryStore Library { get; }
    public AppHost Host => _host;
    public IntPtr Hwnd => _hwnd;

    // =====================================================================================
    // Presenter / desktop switching
    // =====================================================================================

    public bool IsFullscreen => _wantFullscreen;

    public void SetFullscreen(bool fullscreen)
    {
        _wantFullscreen = fullscreen;
        ApplyPresenter();
    }

    private void ApplyPresenter()
    {
        try
        {
            if (_wantFullscreen)
            {
                AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            }
            else
            {
                AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                if (AppWindow.Presenter is OverlappedPresenter p && p.State != OverlappedPresenterState.Maximized)
                    p.Maximize();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not apply window presenter.", ex);
        }
    }

    /// <summary>Dedicated "go to the Windows desktop" action. Lumen keeps running in the background.</summary>
    public void SwitchToDesktop()
    {
        Log.Info("Switching to the Windows desktop.");
        _overlay?.HideOverlay();
        DesktopSwitcher.EnsureExplorerRunning();
        try
        {
            // A fullscreen presenter cannot be minimised; switch to overlapped first.
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            if (AppWindow.Presenter is OverlappedPresenter p) p.Minimize();
        }
        catch (Exception ex)
        {
            Log.Error("Minimise failed; falling back to ShowWindow.", ex);
            DesktopSwitcher.Minimize(_hwnd);
        }
    }

    /// <summary>Restore the shell from the desktop, a game, the hotkey or a second launch.</summary>
    public void ReturnToShell()
    {
        _overlay?.HideOverlay();
        if (AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized)
            p.Restore();
        ApplyPresenter();
        DesktopSwitcher.BringToForeground(_hwnd);
        Activate();
        _navigator.Resume();
        _ = Library.LoadAsync();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => CurrentPage?.FocusDefault());
    }

    private void EmergencyDesktop()
    {
        Log.Warn("Emergency desktop chord used.");
        _wantFullscreen = false;
        SwitchToDesktop();
        // Make sure the user sees *something* even if explorer was broken.
        DesktopSwitcher.EnsureExplorerRunning();
    }

    // =====================================================================================
    // Quick access overlay
    // =====================================================================================

    public void ToggleQuickAccess()
    {
        _overlay ??= new QuickAccessWindow(this);
        if (_overlay.IsOpen) _overlay.HideOverlay();
        else _overlay.ShowOverlay();
    }

    private bool IsShellForeground() =>
        DesktopSwitcher.IsForeground(_hwnd) || (_overlay?.IsOpen == true && DesktopSwitcher.IsForeground(_overlay.Hwnd));

    // =====================================================================================
    // Navigation
    // =====================================================================================

    private IShellPage? CurrentPage => ContentFrame.Content as IShellPage;

    public void NavigateTab(int index)
    {
        index = Math.Clamp(index, 0, TabPages.Length - 1);
        if (index == _tab && ContentFrame.Content is not SafeModePage) { CurrentPage?.FocusDefault(); return; }
        // Slide in the direction of travel (LB = from left, RB = from right).
        NavigationTransitionInfo transition = Motion.Enabled && _tab >= 0
            ? new SlideNavigationTransitionInfo
            {
                Effect = index > _tab ? SlideNavigationTransitionEffect.FromRight : SlideNavigationTransitionEffect.FromLeft,
            }
            : new SuppressNavigationTransitionInfo();
        _tab = index;
        ContentFrame.Navigate(TabPages[index], null, transition);
        UpdateTabs();
        UpdateHints();
    }

    private void NavigateToSafeMode()
    {
        _tab = -1;
        ContentFrame.Navigate(typeof(SafeModePage), null, new SuppressNavigationTransitionInfo());
        UpdateTabs();
        UpdateHints();
    }

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var i)) NavigateTab(i);
    }

    private void OnDesktopClick(object sender, RoutedEventArgs e) => SwitchToDesktop();

    private void UpdateTabs()
    {
        var accent = (Brush)Application.Current.Resources["LumenAccentBrush"];
        var soft = (Brush)Application.Current.Resources["LumenAccentSoftBrush"];
        var dim = (Brush)Application.Current.Resources["LumenTextDimBrush"];
        Button[] tabs = [HomeTab, LibraryTab, SettingsTab];
        for (var i = 0; i < tabs.Length; i++)
        {
            var selected = i == _tab;
            tabs[i].Foreground = selected ? accent : dim;
            tabs[i].Background = selected ? soft : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    public void UpdateHints()
    {
        HintPanel.Children.Clear();
        if (CurrentPage is { } page)
            foreach (var h in page.Hints) HintPanel.Children.Add(h);
        HintPanel.Children.Add(ButtonHint.Create("⧉ + ≡", "Quick access"));
    }

    // =====================================================================================
    // Controller actions
    // =====================================================================================

    private void OnAction(NavAction action)
    {
        // While the launch curtain is up, only B (dismiss) does anything.
        if (LaunchCurtain.Visibility == Visibility.Visible)
        {
            if (action == NavAction.Back) HideLaunchCurtain(animate: true);
            return;
        }

        if (_overlay?.IsOpen == true && DesktopSwitcher.IsForeground(_overlay.Hwnd))
        {
            _overlay.HandleAction(action);
            return;
        }

        if (CurrentPage?.HandleAction(action) == true) return;

        var root = Content.XamlRoot;
        switch (action)
        {
            case NavAction.Up or NavAction.Down or NavAction.Left or NavAction.Right:
                FocusNavigation.Move(root, action);
                break;
            case NavAction.Accept:
                if (!FocusNavigation.InvokeFocused(root)) CurrentPage?.FocusDefault();
                break;
            case NavAction.Back:
                if (_tab != 0) NavigateTab(0);
                else CurrentPage?.FocusDefault();
                break;
            case NavAction.PreviousTab:
                if (_tab > 0) NavigateTab(_tab - 1);
                break;
            case NavAction.NextTab:
                NavigateTab(_tab < 0 ? 0 : _tab + 1);
                break;
            case NavAction.Menu:
                ToggleQuickAccess();
                break;
        }
    }

    // =====================================================================================
    // Status: clock, battery, controller, toasts
    // =====================================================================================

    public void ShowToast(string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ToastText.Text = message;
            Toast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        });
    }

    public void SetStatus(string text) => DispatcherQueue.TryEnqueue(() => StatusText.Text = text);

    private void UpdateClock() => ClockText.Text = DateTime.Now.ToString("t");

    private void UpdateBattery()
    {
        var status = _host.Battery.Read();
        if (!status.HasBattery || status.Percent is null)
        {
            BatteryPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            BatteryPanel.Visibility = Visibility.Visible;
            var pct = status.Percent.Value;
            BatteryText.Text = $"{pct}%";
            BatteryIcon.Glyph = status.Source == PowerSource.AC
                ? ""                                           // charging
                : char.ConvertFromUtf32(pct >= 95 ? 0xE83F : 0xE850 + Math.Clamp(pct / 10, 0, 9));
            BatteryIcon.Foreground = (Brush)Application.Current.Resources[
                pct <= PowerPolicy.LowBatteryPercent && status.Source != PowerSource.AC ? "LumenBadBrush" : "LumenTextBrush"];
        }

        var profile = PowerPolicy.Choose(status);
        _navigator.SetProfile(profile);
        Motion.PowerAllows = profile.AnimationsEnabled;
    }

    private void UpdateControllerIcon()
    {
        var connected = _host.Gamepad.ConnectedCount > 0;
        ControllerIcon.Foreground = (Brush)Application.Current.Resources[connected ? "LumenTextBrush" : "LumenTextDimBrush"];
        ControllerIcon.Opacity = connected ? 1.0 : 0.5;
    }

    private void OnResumed()
    {
        _navigator.Resume();
        UpdateClock();
        UpdateBattery();
        UpdateControllerIcon();
        if (_wantFullscreen && DesktopSwitcher.IsForeground(_hwnd)) ApplyPresenter();
    }

    // =====================================================================================
    // Launch curtain
    // =====================================================================================

    private DispatcherQueueTimer? _curtainTimeout;
    private DispatcherQueueTimer? _curtainCollapse;

    /// <summary>
    /// Full-screen "starting" moment: the game's art slowly zooms behind its logo/title until the game
    /// window takes the foreground (or 20 s pass, or B is pressed).
    /// </summary>
    public void ShowLaunchCurtain(ViewModels.GameTileViewModel tile)
    {
        _curtainCollapse?.Stop();
        var service = ArtworkService.Instance;
        CurtainArt.Source = ViewModels.GameTileViewModel.CreateBitmap(service?.Cache.TryGetLocal(tile.HeroPath) ?? service?.Cache.TryGetLocal(tile.Game.EffectiveCover), 1280);
        var logo = ViewModels.GameTileViewModel.CreateBitmap(tile.LogoPath, 1100);
        CurtainLogo.Source = logo;
        CurtainLogo.Visibility = logo is null ? Visibility.Collapsed : Visibility.Visible;
        CurtainTitle.Text = tile.Title;
        CurtainTitle.Visibility = logo is null ? Visibility.Visible : Visibility.Collapsed;
        CurtainStatus.Text = $"Starting via {tile.SourceLabel}…";

        CurtainArt.ScaleTransition = null;
        CurtainArt.Scale = System.Numerics.Vector3.One;
        LaunchCurtain.OpacityTransition = Motion.Fade(260);
        LaunchCurtain.Opacity = 0;
        LaunchCurtain.Visibility = Visibility.Visible;
        CurtainRing.IsActive = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            LaunchCurtain.Opacity = 1;
            if (Motion.Enabled)
            {
                CurtainArt.CenterPoint = new System.Numerics.Vector3((float)LaunchCurtain.ActualWidth / 2f, (float)LaunchCurtain.ActualHeight / 2f, 0f);
                CurtainArt.ScaleTransition = new Microsoft.UI.Xaml.Vector3Transition { Duration = TimeSpan.FromSeconds(6) };
                CurtainArt.Scale = new System.Numerics.Vector3(1.1f, 1.1f, 1f);
            }
        });

        _curtainTimeout ??= CreateOneShot(TimeSpan.FromSeconds(20), () => HideLaunchCurtain(animate: true));
        _curtainTimeout.Stop();
        _curtainTimeout.Start();
    }

    public void HideLaunchCurtain(bool animate)
    {
        _curtainTimeout?.Stop();
        if (LaunchCurtain.Visibility != Visibility.Visible) return;
        LaunchCurtain.OpacityTransition = animate ? Motion.Fade(240) : null;
        LaunchCurtain.Opacity = 0;
        _curtainCollapse ??= CreateOneShot(TimeSpan.FromMilliseconds(300), () =>
        {
            if (LaunchCurtain.Opacity > 0) return; // re-shown meanwhile
            LaunchCurtain.Visibility = Visibility.Collapsed;
            CurtainRing.IsActive = false; // no spinner work while hidden
            CurtainArt.Source = null;
            CurtainLogo.Source = null;
        });
        _curtainCollapse.Stop();
        _curtainCollapse.Start();
    }

    private DispatcherQueueTimer CreateOneShot(TimeSpan delay, Action action)
    {
        var t = DispatcherQueue.CreateTimer();
        t.Interval = delay;
        t.IsRepeating = false;
        t.Tick += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { Log.Error("Timer action failed.", ex); }
        };
        return t;
    }

    // =====================================================================================
    // Lifecycle
    // =====================================================================================

    private async Task StartLibraryAsync()
    {
        await Library.LoadAsync();
        if (_host.IsSafeMode) return; // don't run scanners in safe mode

        SetStatus("Looking for games…");
        var report = await Library.RescanAsync();
        SetStatus(string.Empty);
        if (report is null) return;
        if (report.Added > 0) ShowToast($"Found {report.Added} new game{(report.Added == 1 ? "" : "s")}");
        if (report.Failures.Count > 0)
            Log.Warn($"Some sources failed: {string.Join(", ", report.Failures.Keys)}");
    }

    private async Task WarmUpHardwareAsync()
    {
        try
        {
            await Task.Delay(1500); // let the first frame settle
            await _host.Hardware.Value;
        }
        catch (Exception ex)
        {
            Log.Error("Hardware initialisation failed.", ex);
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            // The game (or its launcher) took the foreground: the curtain has done its job.
            if (LaunchCurtain.Visibility == Visibility.Visible) HideLaunchCurtain(animate: false);
            return;
        }
        UpdateClock();
        UpdateBattery();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Log.Info("Main window closed - shutting down.");
        _clockTimer.Stop();
        _batteryTimer.Stop();
        _stableTimer.Stop();
        _navigator.Dispose();
        _hook.Dispose();
        _host.Power.Dispose();
        _overlay?.CloseForGood();
        if (_host.Hardware.IsValueCreated && _host.Hardware.Value.IsCompletedSuccessfully)
            _host.Hardware.Value.Result.Monitor?.Dispose();
        _host.Guard.MarkCleanExit();
        Application.Current.Exit();
    }

    /// <summary>Exit Lumen completely (the desktop is always available afterwards).</summary>
    public void ExitShell()
    {
        DesktopSwitcher.EnsureExplorerRunning();
        if (!_closing) Close();
    }

    private DispatcherQueueTimer CreateTimer(TimeSpan interval, Action tick)
    {
        var t = DispatcherQueue.CreateTimer();
        t.Interval = interval;
        t.IsRepeating = true;
        t.Tick += (_, _) =>
        {
            try { tick(); }
            catch (Exception ex) { Log.Error("Timer tick failed.", ex); }
        };
        return t;
    }
}
