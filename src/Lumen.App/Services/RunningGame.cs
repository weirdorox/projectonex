using Lumen.Core.Models;
using Lumen.Platform.Windows.Processes;

namespace Lumen.App.Services;

/// <summary>A library game whose process Lumen has seen and is tracking.</summary>
public sealed record RunningGame(GameEntry Game, ProcessInfo Process, DateTimeOffset Since);
