using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lumen.App.Controls;

/// <summary>
/// Controller-first on-screen keyboard (no Windows touch keyboard needed): a grid of large keys that
/// the D-pad moves across and A presses. Edits a target <see cref="TextBox"/> (or
/// <see cref="PasswordBox"/>) directly. Shift for capitals, "#+=" for symbols (Wi-Fi passwords).
/// Shortcuts handled by the host page: X = backspace, Y = space.
/// </summary>
public sealed class OnScreenKeyboard : StackPanel
{
    private static readonly string[] Rows = ["1234567890", "qwertyuiop", "asdfghjkl'", "zxcvbnm-:."];
    private static readonly string[] SymbolRows = ["!@#$%^&*()", "~`_=+[]{}|", @"\;""<>?/,'é", "€£§°±µ¿¡«»"];
    private readonly List<(Button Key, int Row, int Col)> _keys = [];
    private readonly Button _shiftKey;
    private readonly Button _symbolKey;
    private bool _shift;
    private bool _symbols;

    public OnScreenKeyboard() : this(48) { }

    public OnScreenKeyboard(double keyWidth)
    {
        Spacing = 6;
        for (var r = 0; r < Rows.Length; r++)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            for (var c = 0; c < Rows[r].Length; c++)
            {
                var key = MakeKey("", keyWidth);
                key.Click += (s, _) => TypeKey((string)((Button)s!).Content);
                _keys.Add((key, r, c));
                panel.Children.Add(key);
            }
            Children.Add(panel);
        }

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _shiftKey = MakeKey("⇧", keyWidth * 1.3);
        _shiftKey.Click += (_, _) => { _shift = !_shift; Relabel(); };
        _symbolKey = MakeKey("#+=", keyWidth * 1.3);
        _symbolKey.Click += (_, _) => { _symbols = !_symbols; Relabel(); };
        var space = MakeKey("space", keyWidth * 2.6);
        space.Click += (_, _) => Type(" ");
        var back = MakeKey("⌫", keyWidth * 1.3);
        back.Click += (_, _) => Backspace();
        var done = MakeKey("done", keyWidth * 1.7);
        done.Click += (_, _) => Done?.Invoke(this, EventArgs.Empty);
        foreach (var b in new[] { _shiftKey, _symbolKey, space, back, done }) bottom.Children.Add(b);
        Children.Add(bottom);
        FirstKey = _keys.First(k => k.Row == 1 && k.Col == 0).Key;
        Relabel();
    }

    /// <summary>A TextBox or PasswordBox.</summary>
    public Control? Target { get; set; }

    /// <summary>The 'q' key - a sensible place for initial focus.</summary>
    public Button FirstKey { get; }

    public event EventHandler? Done;

    private string Text
    {
        get => Target switch { TextBox t => t.Text ?? "", PasswordBox p => p.Password ?? "", _ => "" };
        set
        {
            switch (Target)
            {
                case TextBox t:
                    t.Text = value;
                    t.SelectionStart = value.Length;
                    break;
                case PasswordBox p:
                    p.Password = value;
                    break;
            }
        }
    }

    private int MaxLength => Target switch { TextBox { MaxLength: > 0 } t => t.MaxLength, PasswordBox { MaxLength: > 0 } p => p.MaxLength, _ => 64 };

    public void Type(string text)
    {
        if (Target is null) return;
        var t = Text;
        if (t.Length + text.Length > MaxLength) return;
        Text = t + text;
    }

    private void TypeKey(string text)
    {
        Type(text);
        if (_shift && !_symbols) { _shift = false; Relabel(); }
    }

    public void Backspace()
    {
        var t = Text;
        if (t.Length > 0) Text = t[..^1];
    }

    private void Relabel()
    {
        foreach (var (key, row, col) in _keys)
        {
            var ch = (_symbols ? SymbolRows : Rows)[row][col].ToString();
            key.Content = _shift && !_symbols ? ch.ToUpperInvariant() : ch;
        }
        _symbolKey.Content = _symbols ? "abc" : "#+=";
        _shiftKey.Opacity = _shift ? 1 : 0.75;
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
