using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Lumen.Core.Diagnostics;
using Lumen.Core.Updates;
using Lumen.Platform.Windows.Security;
using Microsoft.UI.Dispatching;

namespace Lumen.App.Services;

public enum UpdateStatus { Idle, Checking, UpToDate, Available, Downloading, Ready, Installing, Failed }

/// <summary>
/// In-app updates from GitHub Releases (see docs/UPDATES.md):
/// check → signed metadata (ECDSA, Lumen's own key) → download with resume → SHA-256 + size (+ Authenticode
/// when signed) → run the verified Inno Setup installer as a separate process → Lumen exits → the
/// installer restarts Lumen. The previous version's verified installer is kept for rollback.
/// Never installs or restarts while a game is running.
/// </summary>
public sealed class UpdateService
{
    private static readonly HttpClient Http = CreateClient();
    private readonly AppHost _host;
    private readonly MainWindow _shell;
    private readonly DispatcherQueue _ui;
    private readonly string _dir;
    private readonly string _journalPath;
    private DispatcherQueueTimer? _timer;
    private string? _etag;
    private string? _releasesCache;
    private CancellationTokenSource? _download;

    public UpdateService(AppHost host, MainWindow shell, DispatcherQueue ui)
    {
        _host = host;
        _shell = shell;
        _ui = ui;
        _dir = Path.Combine(host.Paths.Root, "updates");
        _journalPath = Path.Combine(_dir, "journal.json");
        Current = CurrentVersion();
    }

    public SemVersion Current { get; }
    public UpdateStatus Status { get; private set; } = UpdateStatus.Idle;
    public string Message { get; private set; } = "";
    public double Progress { get; private set; }
    public GitHubRelease? Latest { get; private set; }
    public UpdateManifest? Manifest { get; private set; }
    public string? InstallerPath { get; private set; }
    public bool CanRollBack => File.Exists(UpdateJournal.Load(_journalPath).PreviousInstallerPath ?? "");

    private static string Hash(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s));
    }
    public string? PreviousVersion => UpdateJournal.Load(_journalPath).FromVersion;

    /// <summary>Raised on the UI thread whenever status/progress changes.</summary>
    public event EventHandler? Changed;

    public static SemVersion CurrentVersion()
    {
        var info = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return SemVersion.Parse(info) ?? SemVersion.Parse(typeof(App).Assembly.GetName().Version?.ToString()) ?? new SemVersion(0, 0, 0, "unknown");
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Lumen-Updater/1.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    // ---------------- lifecycle ----------------

    /// <summary>Called once the UI is ready: reconcile a finished/failed install, then schedule checks.</summary>
    public void Start()
    {
        if (_host.IsSafeMode) return;
        try
        {
            var journal = UpdateJournal.Load(_journalPath);
            var phase = journal.Reconcile(Current);
            if (journal.Phase != UpdatePhase.None) journal.Save(_journalPath);
            switch (phase)
            {
                case UpdatePhase.Installed:
                    Set(UpdateStatus.UpToDate, $"Updated to Lumen {Current}.");
                    _shell.ShowToast($"Lumen updated to {Current}");
                    // Startup validation: if this version keeps running, the update is confirmed.
                    _ = ConfirmAfterDelayAsync();
                    break;
                case UpdatePhase.Failed:
                    Set(UpdateStatus.Failed, (journal.Error ?? "The update did not install.") + (journal.LogPath is { } log ? $" Log: {log}" : ""));
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Reading the update journal failed.", ex);
        }

        _timer = _ui.CreateTimer();
        _timer.Interval = TimeSpan.FromHours(1);
        _timer.Tick += (_, _) => _ = AutoCheckAsync();
        _timer.Start();
        _ = Task.Delay(TimeSpan.FromSeconds(25)).ContinueWith(_ => _ui.TryEnqueue(() => _ = AutoCheckAsync()));
    }

    private async Task ConfirmAfterDelayAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(60));
        var journal = UpdateJournal.Load(_journalPath);
        if (journal.Phase != UpdatePhase.Installed) return;
        journal.Phase = UpdatePhase.None; // validated; previous installer stays for manual rollback
        journal.Save(_journalPath);
        TrimDownloads(keep: [journal.InstallerPath, journal.PreviousInstallerPath]);
        Log.Info($"Update to {Current} validated (ran 60 s).");
    }

    private async Task AutoCheckAsync()
    {
        var last = _host.Settings.LastUpdateCheck;
        if (last is { } l && DateTimeOffset.UtcNow - l < TimeSpan.FromHours(24)) return;
        if (Status is UpdateStatus.Downloading or UpdateStatus.Installing or UpdateStatus.Checking) return;
        await CheckAsync(manual: false);
        // Subtle notification only - never while a game runs or over it.
        if (Status == UpdateStatus.Available && _shell.RunningGame is null && _shell.IsDashboardInFront)
            _shell.ShowToast($"Lumen {Latest?.Version} is available - Settings › Updates");
    }

    // ---------------- check ----------------

    public async Task CheckAsync(bool manual = true)
    {
        if (Status is UpdateStatus.Downloading or UpdateStatus.Installing) return;
        Set(UpdateStatus.Checking, "Checking for updates…");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UpdateRules.ReleasesApi);
            if (_etag is not null) request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(_etag));
            using var response = await Http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotModified && _releasesCache is not null)
            {
                // unchanged - reuse cached list (does not count against the rate limit)
            }
            else if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                var reset = response.Headers.TryGetValues("X-RateLimit-Reset", out var v) && long.TryParse(v.FirstOrDefault(), out var unix)
                    ? DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("t") : null;
                Set(UpdateStatus.Failed, "GitHub is limiting update checks right now" + (reset is null ? "." : $" - try again after {reset}."));
                return;
            }
            else if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Set(UpdateStatus.Failed, "The update source is not reachable (the GitHub repository must be public).");
                return;
            }
            else
            {
                response.EnsureSuccessStatusCode();
                _releasesCache = await response.Content.ReadAsStringAsync();
                _etag = response.Headers.ETag?.Tag;
            }
            _host.Settings.LastUpdateCheck = DateTimeOffset.UtcNow;

            var releases = UpdateRules.ParseReleases(_releasesCache!);
            var newest = UpdateRules.Newest(releases, Current, _host.Settings.UpdateChannel);
            if (newest is null)
            {
                Latest = null;
                Manifest = null;
                Set(UpdateStatus.UpToDate, $"Lumen {Current} is up to date ({(_host.Settings.UpdateChannel == UpdateChannel.Beta ? "beta" : "stable")} channel).");
                return;
            }

            var (manifest, error) = await FetchVerifiedManifestAsync(newest, allowDowngrade: false);
            if (manifest is null)
            {
                Set(UpdateStatus.Failed, $"Lumen {newest.Version} was found but rejected: {error}");
                return;
            }
            Latest = newest;
            Manifest = manifest;
            InstallerPath = Path.Combine(_dir, manifest.Installer);
            if (File.Exists(InstallerPath) && await VerifyFileAsync(InstallerPath, manifest) is null)
                Set(UpdateStatus.Ready, $"Lumen {newest.Version} is downloaded and verified.");
            else
                Set(UpdateStatus.Available, $"Lumen {newest.Version} is available ({Size(manifest.Size)}).");
        }
        catch (HttpRequestException ex)
        {
            Set(UpdateStatus.Failed, "No connection to GitHub. Check the network and try again.");
            Log.Warn("Update check failed.", ex);
        }
        catch (TaskCanceledException)
        {
            Set(UpdateStatus.Failed, "The update check timed out.");
        }
        catch (Exception ex)
        {
            Log.Error("Update check failed.", ex);
            Set(UpdateStatus.Failed, "The update check failed: " + ex.Message);
        }
    }

    private static async Task<(UpdateManifest? Manifest, string? Error)> FetchVerifiedManifestAsync(GitHubRelease release, bool allowDowngrade, SemVersion? current = null)
    {
        var mAsset = release.Asset(UpdateRules.ManifestAsset)!;
        var sAsset = release.Asset(UpdateRules.SignatureAsset)!;
        if (!UpdateRules.IsTrustedUrl(mAsset.Url) || !UpdateRules.IsTrustedUrl(sAsset.Url)) return (null, "metadata is not hosted in Lumen's releases");
        var manifestBytes = await Http.GetByteArrayAsync(mAsset.Url);
        var signature = await Http.GetByteArrayAsync(sAsset.Url);
        if (!UpdateRules.VerifySignature(manifestBytes, signature)) return (null, "the release signature is invalid (not signed with Lumen's release key)");
        var manifest = UpdateRules.ParseManifest(manifestBytes);
        if (manifest is null) return (null, "the update metadata is unreadable");
        var error = UpdateRules.Validate(manifest, release, current ?? CurrentVersion(), allowDowngrade);
        return error is null ? (manifest, null) : (null, error);
    }

    // ---------------- download ----------------

    public async Task DownloadAsync()
    {
        if (Manifest is not { } m || Latest is not { } release || Status == UpdateStatus.Downloading) return;
        _download = new CancellationTokenSource();
        try
        {
            InstallerPath = await DownloadInstallerAsync(release, m, _download.Token, p =>
            {
                Progress = p;
                Set(UpdateStatus.Downloading, $"Downloading Lumen {release.Version}… {p:P0}");
            });
            Set(UpdateStatus.Ready, $"Lumen {release.Version} is downloaded and verified. Ready to install.");
        }
        catch (OperationCanceledException)
        {
            Set(UpdateStatus.Available, "Download paused - it continues where it stopped.");
        }
        catch (Exception ex)
        {
            Log.Warn("Update download failed.", ex);
            Set(UpdateStatus.Failed, ex is UpdateRejectedException ? ex.Message : "The download failed - it resumes where it stopped when you retry.");
        }
    }

    public void CancelDownload() => _download?.Cancel();

    private async Task<string> DownloadInstallerAsync(GitHubRelease release, UpdateManifest m, CancellationToken ct, Action<double>? progress)
    {
        Directory.CreateDirectory(_dir);
        var target = Path.Combine(_dir, m.Installer);
        if (File.Exists(target) && await VerifyFileAsync(target, m) is null) return target;

        var partial = target + ".partial";
        var asset = release.Asset(m.Installer)!;
        long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (have > m.Size) { File.Delete(partial); have = 0; }

        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (have > 0 && response.StatusCode != HttpStatusCode.PartialContent) have = 0; // server ignored the range

        await using (var file = new FileStream(partial, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var body = await response.Content.ReadAsStreamAsync(ct))
        {
            var buffer = new byte[81920];
            long done = have;
            var lastReport = Stopwatch.StartNew();
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (done > m.Size) throw new UpdateRejectedException("The download is larger than the signed size - rejected.");
                if (lastReport.ElapsedMilliseconds > 200)
                {
                    lastReport.Restart();
                    var p = (double)done / m.Size;
                    _ui.TryEnqueue(() => progress?.Invoke(p));
                }
            }
        }

        var problem = await VerifyFileAsync(partial, m);
        if (problem is not null)
        {
            File.Delete(partial); // corrupted or tampered: never kept
            throw new UpdateRejectedException(problem);
        }
        File.Move(partial, target, overwrite: true);
        Log.Info($"Update {m.Version} downloaded and verified ({m.Size} bytes).");
        return target;
    }

    /// <summary>Size + SHA-256 from the signed manifest, plus Authenticode when the installer is signed.</summary>
    private static async Task<string?> VerifyFileAsync(string path, UpdateManifest m)
    {
        return await Task.Run(() =>
        {
            var info = new FileInfo(path);
            if (info.Length != m.Size) return "The download is incomplete or corrupted.";
            using (var stream = File.OpenRead(path))
                if (!UpdateRules.ChecksumMatches(stream, m.Sha256)) return "The download does not match the signed checksum - rejected.";
            var (signed, valid, _) = Authenticode.Inspect(path);
            if (signed && !valid) return "The installer's code signature is invalid - rejected.";
            return null;
        });
    }

    // ---------------- install ----------------

    public string? InstallBlockedReason =>
        _shell.RunningGame is { } g ? $"Close {g.Game.Title} first - Lumen never installs updates while a game is running."
        : Status != UpdateStatus.Ready || InstallerPath is null ? "Download the update first."
        : null;

    public async Task InstallAndRestartAsync()
    {
        if (InstallBlockedReason is { } blocked)
        {
            Set(Status, blocked);
            return;
        }
        var m = Manifest!;
        Set(UpdateStatus.Installing, "Preparing the update…");
        try
        {
            // Re-verify right before running it (the file could have changed on disk).
            if (await VerifyFileAsync(InstallerPath!, m) is { } problem)
            {
                Set(UpdateStatus.Failed, problem);
                return;
            }
            var previous = await EnsureCurrentInstallerAsync();
            var log = Path.Combine(_dir, $"install-{m.Version}.log");
            new UpdateJournal
            {
                Phase = UpdatePhase.Installing,
                FromVersion = Current.ToString(),
                ToVersion = m.Version,
                InstallerPath = InstallerPath,
                PreviousInstallerPath = previous,
                PreviousInstallerSha256 = previous is null ? null : await Task.Run(() => Hash(previous)),
                LogPath = log,
                StartedAt = DateTimeOffset.UtcNow,
            }.Save(_journalPath);

            RunInstaller(InstallerPath!, log);
            Set(UpdateStatus.Installing, "Installing - Lumen restarts in a moment…");
            Log.Info($"Installing update {Current} -> {m.Version}; Lumen exits now.");
            await Task.Delay(400);
            _shell.ExitShell();
        }
        catch (Exception ex)
        {
            Log.Error("Starting the update failed.", ex);
            var journal = UpdateJournal.Load(_journalPath);
            if (journal.Phase == UpdatePhase.Installing)
            {
                journal.Phase = UpdatePhase.Failed;
                journal.Error = ex.Message;
                journal.Save(_journalPath);
            }
            Set(UpdateStatus.Failed, ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 }
                ? "The installation was cancelled at the Windows permission prompt."
                : "The installer could not be started: " + ex.Message);
        }
    }

    /// <summary>
    /// Runs the verified installer as its own process (Lumen never overwrites its running files).
    /// Silent Inno Setup install over the existing one: settings, library and profiles in
    /// %LOCALAPPDATA%\Lumen are never touched. /LUMENUPDATE=1 makes the installer start Lumen again.
    /// For an all-users install Windows shows its normal UAC prompt.
    /// </summary>
    private static void RunInstaller(string installer, string log)
    {
        var psi = new ProcessStartInfo(installer)
        {
            UseShellExecute = true,
            Arguments = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /SP- /LUMENUPDATE=1 /LOG=\"{log}\"",
            WorkingDirectory = Path.GetDirectoryName(installer)!,
        };
        using var _ = Process.Start(psi) ?? throw new InvalidOperationException("The installer did not start.");
    }

    /// <summary>Keeps a verified installer of the version running now, so an update can be undone.</summary>
    private async Task<string?> EnsureCurrentInstallerAsync()
    {
        try
        {
            var name = UpdateRules.InstallerName(Current);
            var path = Path.Combine(_dir, name);
            if (File.Exists(path)) return path;
            if (_releasesCache is null || UpdateRules.ForVersion(UpdateRules.ParseReleases(_releasesCache), Current) is not { } release) return null;
            var (manifest, _) = await FetchVerifiedManifestAsync(release, allowDowngrade: true);
            return manifest is null ? null : await DownloadInstallerAsync(release, manifest, CancellationToken.None, null);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not keep the current version's installer for rollback.", ex);
            return null;
        }
    }

    /// <summary>Reinstalls the previous version with its own verified installer.</summary>
    /// <summary>Also used by safe mode and Lumen Recovery (when the new version does not start).</summary>
    public string? RollBack()
    {
        var journal = UpdateJournal.Load(_journalPath);
        if (journal.VerifiedPreviousInstaller() is null) return "No verified previous version is available to roll back to.";
        if (_shell.RunningGame is not null) return "Close the game first.";
        try
        {
            var log = Path.Combine(_dir, "rollback.log");
            journal.Phase = UpdatePhase.Installing;
            (journal.FromVersion, journal.ToVersion) = (journal.ToVersion, journal.FromVersion);
            (journal.InstallerPath, journal.PreviousInstallerPath) = (journal.PreviousInstallerPath, journal.InstallerPath);
            journal.PreviousInstallerSha256 = journal.PreviousInstallerPath is { } p && File.Exists(p) ? Hash(p) : null;
            journal.LogPath = log;
            journal.StartsSinceInstall = 0;
            journal.Error = null;
            journal.Save(_journalPath);
            RunInstaller(journal.InstallerPath!, log);
            _shell.ExitShell();
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Rollback failed to start.", ex);
            return "The previous installer could not be started: " + ex.Message;
        }
    }

    private void TrimDownloads(IEnumerable<string?> keep)
    {
        try
        {
            var keepSet = keep.Where(k => k is not null).Select(k => Path.GetFullPath(k!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(_dir, "Lumen-Setup-*.exe*"))
                if (!keepSet.Contains(Path.GetFullPath(f))) File.Delete(f);
        }
        catch (Exception ex)
        {
            Log.Warn("Cleaning old update downloads failed.", ex);
        }
    }

    private void Set(UpdateStatus status, string message)
    {
        Status = status;
        Message = message;
        if (status != UpdateStatus.Downloading) Progress = status == UpdateStatus.Ready ? 1 : 0;
        if (_ui.HasThreadAccess) Changed?.Invoke(this, EventArgs.Empty);
        else _ui.TryEnqueue(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    public static string Size(long bytes) => bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.0} MB" : $"{bytes / 1024.0:0} KB";
}

public sealed class UpdateRejectedException(string message) : Exception(message);
