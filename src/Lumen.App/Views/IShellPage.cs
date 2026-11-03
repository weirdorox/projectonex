using Lumen.App.Controls;
using Lumen.Core.Input;

namespace Lumen.App.Views;

/// <summary>Implemented by every page hosted in the main shell.</summary>
public interface IShellPage
{
    /// <summary>Page-specific handling. Return true if handled; otherwise the shell applies default focus/invoke behaviour.</summary>
    bool HandleAction(NavAction action);

    /// <summary>Puts focus on the most useful element (first tile, first setting).</summary>
    void FocusDefault();

    /// <summary>Button legend shown in the bottom bar for this page.</summary>
    IReadOnlyList<ButtonHint> Hints { get; }
}
