using Microsoft.UI.Xaml.Documents;

namespace Bwc.UI.Dialogs;

/// <summary>
/// Win32 mnemonic text. An ampersand before a character marks that character as the access key,
/// "&amp;&amp;" is a literal ampersand, and the text shown never includes the marker. The access key is
/// underlined only while Alt is held, which is how Windows shows keyboard cues by default.
/// </summary>
internal sealed class ClassicMnemonicText
{
    private readonly int _mnemonicIndex;

    internal ClassicMnemonicText(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var plain = new System.Text.StringBuilder(source.Length);
        int index = -1;
        for (int i = 0; i < source.Length; i++)
        {
            char current = source[i];
            if (current != '&')
            {
                plain.Append(current);
                continue;
            }

            if (i + 1 >= source.Length)
            {
                // A trailing ampersand has no character after it and is dropped, as Win32 drops it.
                break;
            }

            if (source[i + 1] == '&')
            {
                plain.Append('&');
                i++;
                continue;
            }

            if (index < 0)
            {
                index = plain.Length;
                char letter = source[i + 1];
                plain.Append(letter);
                Mnemonic = char.ToUpperInvariant(letter);
                i++;
                continue;
            }

            // A second marker is not a mnemonic. Its character stays in the text.
            plain.Append(source[i + 1]);
            i++;
        }

        Plain = plain.ToString();
        _mnemonicIndex = index;
    }

    /// <summary>The text as shown and read by automation, with markers removed.</summary>
    internal string Plain { get; }

    /// <summary>The access key in upper case, or null when the text has no mnemonic.</summary>
    internal char? Mnemonic { get; }

    /// <summary>Replaces the inlines with the text, underlining the access key when <paramref name="cue"/> is true.</summary>
    internal void Render(InlineCollection inlines, bool cue)
    {
        ArgumentNullException.ThrowIfNull(inlines);

        inlines.Clear();
        if (_mnemonicIndex < 0 || Plain.Length == 0)
        {
            inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = Plain });
            return;
        }

        if (_mnemonicIndex > 0)
        {
            inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = Plain[.._mnemonicIndex] });
        }

        string letter = Plain.Substring(_mnemonicIndex, 1);
        if (cue)
        {
            var underline = new Underline();
            underline.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = letter });
            inlines.Add(underline);
        }
        else
        {
            inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = letter });
        }

        if (_mnemonicIndex + 1 < Plain.Length)
        {
            inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = Plain[(_mnemonicIndex + 1)..] });
        }
    }
}
