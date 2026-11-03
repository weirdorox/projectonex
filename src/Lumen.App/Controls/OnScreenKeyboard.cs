using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lumen.App.Controls;

/// <summary>
/// Controller-first on-screen keyboard (no Windows touch keyboard needed): a grid of large keys that
/// the D-pad moves across and A presses. Edits a target <see cref="TextBox"/> directly.
/// Shortcuts handled by the host page: X = backspace, Y = space.
/// </summary>
public sealed class OnScreenKeyboard : StackPanel
{
    private static readonly string[] Rows = ["1234567890", "qwertyuiop", "asdfghjkl'", "zxcvbnm-:."];
    private readonly List<Button> _letters = [];
    private bool _shift;

    public OnScreenKeyboard()
    {
        Spacing = 6;
        foreach (var row in Rows)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var ch in row)
            {
                var key = MakeKey(ch.ToString(), 48);
                var c = ch;
                key.Click += (_, _) => Type(_shift ? char.ToUpperInvariant(c).ToString() : c.ToString());
                if (char.IsLetter(ch)) _letters.Add(key);
                panel.Children.Add(key);
            }
            Children.Add(panel);
        }

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var shift = MakeKey("⇧", 72);
        shift.Click += (_, _) => ToggleShift();
        var space = MakeKey("space", 180);
        space.Click += (_, _) => Type(" ");
        var back = MakeKey("⌫", 72);
        back.Click += (_, _) => Backspace();
        var clear = MakeKey("clear", 90);
        clear.Click += (_, _) => { if (Target is not null) Target.Text = string.Empty; };
        var done = MakeKey("done", 90);
        done.Click += (_, _) => Done?.Invoke(this, EventArgs.Empty);
        foreach (var b in new[] { shift, space, back, clear, done }) bottom.Children.Add(b);
        Children.Add(bottom);
        FirstKey = (Button)((StackPanel)Children[1]).Children[0];
    }

    public TextBox? Target { get; set; }

    /// <summary>The 'q' key - a sensible place for initial focus.</summary>
    public Button FirstKey { get; }

    public event EventHandler? Done;

    public void Type(string text)
    {
        if (Target is null) return;
        var t = Target.Text ?? string.Empty;
        if (t.Length + text.Length > (Target.MaxLength > 0 ? Target.MaxLength : 64)) return;
        Target.Text = t + text;
        Target.SelectionStart = Target.Text.Length;
        if (_shift) ToggleShift();
    }

    public void Backspace()
    {
        if (Target is null || string.IsNullOrEmpty(Target.Text)) return;
        Target.Text = Target.Text[..^1];
        Target.SelectionStart = Target.Text.Length;
    }

    private void ToggleShift()
    {
        _shift = !_shift;
        foreach (var b in _letters)
        {
            var s = (string)b.Content;
            b.Content = _shift ? s.ToUpperInvariant() : s.ToLowerInvariant();
        }
    }

    private static Button MakeKey(string label, double width) => new()
    {
        Content = label,
        Width = width,
        Height = 48,
        MinWidth = 0,
        Padding = new Thickness(0),
        FontSize = 17,
        HorizontalContentAlignment = HorizontalAlignment.Center,
    };
}
