namespace Lumen.Core;

/// <summary>
/// All per-user writable locations. Everything lives under one root so that a reset
/// is a single folder delete and nothing is ever written next to the executable.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = root;
    }

    /// <summary>%LOCALAPPDATA%\Lumen on Windows.</summary>
    public static AppPaths ForCurrentUser() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumen"));

    public string Root { get; }
    public string Logs => Path.Combine(Root, "logs");
    public string Database => Path.Combine(Root, "lumen.db");
    public string ArtworkCache => Path.Combine(Root, "artwork");
    public string StartupState => Path.Combine(Root, "startup-state.json");

    /// <summary>
    /// Written by the recovery tool. When present, Lumen starts in safe mode (windowed, no
    /// fullscreen, autostart not re-enabled) and deletes the flag.
    /// </summary>
    public string RecoveryFlag => Path.Combine(Root, "recovery.flag");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(ArtworkCache);
    }
}
