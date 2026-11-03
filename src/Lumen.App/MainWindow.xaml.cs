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
    private static readonly Type[] TabPages = [typeof(HomePage), typeof(LibraryPage), typeof(CollectionsPage), typeof(SearchPage), typeof(SettingsPage)];

    public const int HomeTabIndex = 0, LibraryTabIndex = 1, CollectionsTabIndex = 2, SearchTabIndex = 3, SettingsTabIndex = 4;

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
        Tdp = new TdpService(DispatcherQueue, host.IsSafeMode);
        SmartPower = new SmartPowerService(host, Tdp);
        GameSessions = new GameSessionService(host, this, DispatcherQueue);
        Overlay = new PerformanceOverlayService(host, this, DispatcherQueue);

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
        _hook.SessionUnlocked += () => DispatcherQueue.TryEnqueue(OnResumed);
        _hook.DisplayChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (_wantFullscreen && DesktopSwitcher.IsForeground(_hwnd)) ApplyPresenter();
            ScheduleDockCheck();
        });

        // ---- controller ----
        _navigator = new GamepadNavigator(DispatcherQueue, host.Gamepad, IsShellForeground);
        _navigator.ActionReceived += OnAction;
        _navigator.QuickAccessRequested += ToggleQuickAccess;
        _navigator.EmergencyDesktopRequested += EmergencyDesktop;
        _navigator.ShortcutRequested += OnShortcut;
        MouseMode = new MouseModeService(this, _navigator);
        host.Gamepad.ConnectionChanged += (_, e) =>
        {
            UpdateControllerIcon();
            ShowToast(e.Connected ? "Controller connected" : "Controller disconnected");
        };
        _navigator.Start();

        // ---- power: sleep/resume and display on/off ----
        host.Power.Suspending += (_, _) => DispatcherQueue.TryEnqueue(PrepareForSleep);
        host.Power.Resumed += (_, _) => DispatcherQueue.TryEnqueue(OnResumed);
        host.Power.DisplayStateChanged += (_, on) => DispatcherQueue.TryEnqueue(() =>
        {
            if (on) OnResumed();
            else PrepareForSleep();
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
        // Where you left off: last tab and last focused game survive restarts, crashes and reboots.
        PendingFocusGameId = host.Settings.LastGameId;
        if (host.IsSafeMode) NavigateToSafeMode();
        else NavigateTab(Math.Clamp(host.Settings.LastTab, 0, CollectionsTabIndex));

        _ = StartLibraryAsync();
        _ = WarmUpHardwareAsync();
    }

    public LibraryStore Library { get; }

    /// <summary>The library game currently running, if known (set by the game process watcher).</summary>
    internal RunningGame? RunningGame { get; set; }

    /// <summary>Real TDP control (helper-backed; reports unavailability honestly).</summary>
    internal TdpService Tdp { get; }

    /// <summary>Game to focus once its tile exists (session restore). Consumed by the first page that finds it.</summary>
    internal string? PendingFocusGameId { get; set; }

    /// <summary>Remembers the focused game for session restore.</summary>
    internal void RememberFocusedGame(string gameId)
    {
        if (_host.Settings.LastGameId != gameId) _host.Settings.LastGameId = gameId;
    }

    /// <summary>One-slider battery/performance control.</summary>
    internal SmartPowerService SmartPower { get; }

    /// <summary>Foreground game detection + per-game performance profiles.</summary>
    internal GameSessionService? GameSessions { get; private set; }
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
            // Only switch when the kind actually differs: re-setting the same presenter makes the
            // window flash (noticeable on every wake).
            if (_wantFullscreen)
            {
                if (AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen)
                    AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            }
            else
            {
                if (AppWindow.Presenter.Kind != AppWindowPresenterKind.Overlapped)
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

    internal PerformanceOverlayService Overlay { get; }

    internal MouseModeService MouseMode { get; }

    /// <summary>Display scale for mouse speed (physical pixels per DIP).</summary>
    internal double InputScale => Content?.XamlRoot?.RasterizationScale ?? 1.0;

    /// <summary>Controller shortcuts that work in games too: View+Y screenshot, View+X overlay mode.</summary>
    private void OnShortcut(NavAction action)
    {
        if (_host.IsSafeMode) return;
        switch (action)
        {
            case NavAction.Screenshot:
                if (!Lumen.Platform.Windows.Shell.GameBarCapture.IsEnabled())
                    ShowToast("Game Bar captures are turned off in Windows Settings › Gaming › Captures.");
                else if (Lumen.Platform.Windows.Shell.GameBarCapture.Screenshot() && IsShellForeground())
                    ShowToast("Screenshot saved to Videos › Captures");
                break;
            case NavAction.CycleOverlay:
                Overlay.Cycle();
                if (IsShellForeground()) ShowToast($"Performance overlay: {Overlay.Mode}");
                break;
        }
    }

    internal bool IsShellForegroundForOverlay => IsShellForeground();

    private bool IsShellForeground() =>
        DesktopSwitcher.IsForeground(_hwnd) || (_overlay?.IsOpen == true && DesktopSwitcher.IsForeground(_overlay.Hwnd));

    // =====================================================================================
    // Navigation
    // =====================================================================================

    private IShellPage? CurrentPage => ContentFrame.Content as IShellPage;

    public void NavigateTab(int index)
    {
        index = Math.Clamp(index, 0, TabPages.Length - 1);
        if (index == _tab && ContentFrame.Content?.GetType() == TabPages[index]) { CurrentPage?.FocusDefault(); return; }
        // Slide in the direction of travel (LB = from left, RB = from right).
        NavigationTransitionInfo transition = Motion.Enabled && _tab >= 0
            ? new SlideNavigationTransitionInfo
            {
                Effect = index > _tab ? SlideNavigationTransitionEffect.FromRight : SlideNavigationTransitionEffect.FromLeft,
            }
            : new SuppressNavigationTransitionInfo();
        _tab = index;
        if (index <= CollectionsTabIndex) _host.Settings.LastTab = index;
        ContentFrame.Navigate(TabPages[index], null, transition);
        UpdateTabs();
        UpdateHints();
    }

    /// <summary>Opens the details / profile page for a game; B returns to the tab it came from.</summary>
    public void ShowGameDetails(string gameId)
    {
        NavigationTransitionInfo transition = Motion.Enabled
            ? new DrillInNavigationTransitionInfo()
            : new SuppressNavigationTransitionInfo();
        ContentFrame.Navigate(typeof(GameDetailsPage), gameId, transition);
        UpdateHints();
    }

    public void CloseGameDetails() => NavigateTab(_tab < 0 ? 0 : _tab);

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
        Button[] tabs = [HomeTab, LibraryTab, CollectionsTab, SearchTab, SettingsTab];
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

        ThemeService.Play(action switch
        {
            NavAction.Up or NavAction.Down or NavAction.Left or NavAction.Right => ElementSoundKind.Focus,
            NavAction.Accept => ElementSoundKind.Invoke,
            NavAction.Back => ElementSoundKind.GoBack,
            NavAction.PreviousTab or NavAction.PreviousSection => ElementSoundKind.MovePrevious,
            NavAction.NextTab or NavAction.NextSection => ElementSoundKind.MoveNext,
            NavAction.Menu => ElementSoundKind.Show,
            _ => ElementSoundKind.Focus,
        });

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

        // Battery <-> AC switch: optionally re-apply the Smart Power position stored for the new source.
        if (_lastSource is { } previous && previous != status.Source && status.Source != PowerSource.Unknown
            && _host.Settings.SmartPowerAuto && GameSessions?.Engine?.ActiveProfile is null && !_host.IsSafeMode)
        {
            var position = status.Source == PowerSource.Battery ? _host.Settings.SmartPowerBattery : _host.Settings.SmartPowerAc;
            _ = ApplySmartPowerAsync(position);
        }
        _lastSource = status.Source;

        var profile = PowerPolicy.Choose(status);
        _navigator?.SetProfile(profile); // Activated can fire before the constructor finished
        Motion.PowerAllows = profile.AnimationsEnabled;
        GameSessions?.SetLowPower(!profile.AnimationsEnabled);
    }

    private PowerSource? _lastSource;

    private async Task ApplySmartPowerAsync(int position)
    {
        try
        {
            var result = await SmartPower.ApplyAsync(position);
            ShowToast($"Smart Power: {result.Plan.Label} ({string.Join(", ", result.Applied)})");
        }
        catch (Exception ex)
        {
            Log.Error("Automatic Smart Power switch failed.", ex);
        }
    }

    /// <summary>Restarts the Lumen interface (after this instance has exited, so single-instance redirection does not swallow it).</summary>
    public void RestartShell()
    {
        try
        {
            var exe = AppHost.ExecutablePath;
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                $"/c ping -n 3 127.0.0.1 >nul & start \"\" \"{exe}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("Could not schedule the Lumen restart.", ex);
            ShowToast("Restart failed - open Lumen from the Start menu.");
            return;
        }
        ExitShell();
    }

    private void UpdateControllerIcon()
    {
        var connected = _host.Gamepad.ConnectedCount > 0;
        ControllerIcon.Foreground = (Brush)Application.Current.Resources[connected ? "LumenTextBrush" : "LumenTextDimBrush"];
        ControllerIcon.Opacity = connected ? 1.0 : 0.5;
    }

    private readonly System.Diagnostics.Stopwatch _uptime = System.Diagnostics.Stopwatch.StartNew();
    private TimeSpan? _lastResume;
    private bool _sleeping;

    /// <summary>
    /// Display off / suspend: stop all polling and clear transient UI so the first frame after
    /// wake is already the clean, final screen.
    /// </summary>
    private void PrepareForSleep()
    {
        _sleeping = true;
        _navigator.Pause();
        GameSessions?.Pause();
        _overlay?.HideOverlay();
        HideLaunchCurtain(animate: false);
        Toast.Visibility = Visibility.Collapsed;
        Log.Info("Prepared for sleep.");
    }

    /// <summary>
    /// Wake / display on / unlock. Ordered for perceived speed: input first, then what is on screen,
    /// then hardware re-assertion, then deferred work. Duplicate notifications within 2 s are ignored.
    /// </summary>
    private void OnResumed()
    {
        try
        {
            OnResumedCore();
        }
        catch (Exception ex)
        {
            // Runs from dispatcher callbacks: an escaping exception would end the process natively.
            Log.Error("Resume handling failed.", ex);
        }
    }

    private void OnResumedCore()
    {
        var now = _uptime.Elapsed;
        if (!_sleeping && _lastResume is { } last && now - last < TimeSpan.FromSeconds(2)) return;
        _lastResume = now;
        _sleeping = false;
        var timer = System.Diagnostics.Stopwatch.StartNew();

        // 1. Controller: fast re-probe burst, held buttons ignored.
        _navigator.Resume();

        // 2. What the user sees first.
        UpdateClock();
        UpdateBattery();
        UpdateControllerIcon();
        if (_wantFullscreen && DesktopSwitcher.IsForeground(_hwnd)) ApplyPresenter();
        var inputReady = timer.ElapsedMilliseconds;

        void FirstFrame(object? s, object e)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= FirstFrame;
            Log.Info($"Resume: input ready after {inputReady} ms, first frame after {timer.ElapsedMilliseconds} ms.");
        }
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += FirstFrame;

        // 3. Hardware re-assertion off the critical path (drivers may reset display modes / power limits).
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            GameSessions?.Resume();
            _ = Tdp.ResumeAsync();
            if (Content.XamlRoot is { } root && FocusNavigation.Focused(root) is null) CurrentPage?.FocusDefault();
        });
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
        if (GameSessions is not null) await GameSessions.StartAsync();
        Overlay.Start();
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
            await EvaluateDockAsync();
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
        Overlay.Shutdown();
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
