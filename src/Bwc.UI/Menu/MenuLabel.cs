using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Bwc.UI.Menu;

/// <summary>
/// Menu item text with the classic mnemonic underline. The underline is drawn under the first
/// character that matches <see cref="MnemonicKey"/>, and only while <see cref="ClassicMenuState.ShowMnemonics"/>
/// is true. The menu templates use this control for the bar titles, the leaf labels, and the submenu labels.
/// </summary>
public sealed class MenuLabel : UserControl
{
    // TextBlock is sealed, so the label hosts one. Foreground and font are inherited from this control.
    private readonly TextBlock _text = new();

    /// <summary>The visible text, without the ampersand marker.</summary>
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label),
        typeof(string),
        typeof(MenuLabel),
        new PropertyMetadata(string.Empty, OnInputChanged));

    /// <summary>The access key. Its first case-insensitive match in the label is underlined.</summary>
    public static readonly DependencyProperty MnemonicKeyProperty = DependencyProperty.Register(
        nameof(MnemonicKey),
        typeof(string),
        typeof(MenuLabel),
        new PropertyMetadata(null, OnInputChanged));

    public MenuLabel()
    {
        Content = _text;
        Loaded += OnLabelLoaded;
        Unloaded += OnLabelUnloaded;
        Rebuild();
    }

    /// <summary>The visible text, without the ampersand marker.</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>The access key. Its first case-insensitive match in the label is underlined.</summary>
    public string? MnemonicKey
    {
        get => (string?)GetValue(MnemonicKeyProperty);
        set => SetValue(MnemonicKeyProperty, value);
    }

    /// <summary>Index of the underlined character in <see cref="Label"/>, or -1 when there is none.</summary>
    public int MnemonicIndex => FindMnemonic(Label, MnemonicKey);

    private static void OnInputChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is MenuLabel label)
        {
            label.Rebuild();
        }
    }

    private void OnLabelLoaded(object sender, RoutedEventArgs e)
    {
        ClassicMenuState.MnemonicsChanged += OnMnemonicsChanged;
        Rebuild();
    }

    private void OnLabelUnloaded(object sender, RoutedEventArgs e)
    {
        ClassicMenuState.MnemonicsChanged -= OnMnemonicsChanged;
    }

    private void OnMnemonicsChanged(object? sender, EventArgs e)
    {
        Rebuild();
    }

    private void Rebuild()
    {
        var inlines = _text.Inlines;
        inlines.Clear();
        string text = Label ?? string.Empty;
        int index = FindMnemonic(text, MnemonicKey);
        if (index < 0 || !ClassicMenuState.ShowMnemonics)
        {
            inlines.Add(new Run { Text = text });
            return;
        }

        if (index > 0)
        {
            inlines.Add(new Run { Text = text[..index] });
        }

        var underlined = new Underline();
        underlined.Inlines.Add(new Run { Text = text.Substring(index, 1) });
        inlines.Add(underlined);

        if (index + 1 < text.Length)
        {
            inlines.Add(new Run { Text = text[(index + 1)..] });
        }
    }

    /// <summary>
    /// The first character equal to the access key, with its case. The resource keeps the mnemonic's
    /// case, so "Connect &amp;Network" finds the N of Network, not the n of Connect. A case-insensitive
    /// match is the fallback for a key whose case differs from the label.
    /// </summary>
    private static int FindMnemonic(string? text, string? key)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(key))
        {
            return -1;
        }

        int exact = text.IndexOf(key[0]);
        return exact >= 0 ? exact : text.IndexOf(key[0].ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
