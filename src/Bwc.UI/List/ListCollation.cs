using System.Runtime.InteropServices;

namespace Bwc.UI.List;

/// <summary>
/// The name order of the classic list view. Classic compares value names with the native
/// collation at the user locale, ignoring case. Digit runs compare as text ("1, 10, 2"), and
/// symbols sort before digits in the native symbol order ("#h, (x), [y], _x, ~t, 0z"). Ordinal and
/// managed culture comparisons give a different order, so the native call is used.
/// </summary>
internal static partial class ListCollation
{
    private const uint LocaleUserDefault = 0x0400;
    private const uint NormIgnoreCase = 0x00000001;
    private const int CompareEqual = 2;

    /// <summary>The name the Registry Editor shows for the default (empty name) value.</summary>
    internal const string DefaultValueName = "(Default)";

    /// <summary>
    /// Sort key for a row name. The default value keys as the empty string, so it compares before
    /// every other name, as the classic list shows it first.
    /// </summary>
    internal static string SortKey(string name) =>
        string.Equals(name, DefaultValueName, StringComparison.Ordinal) ? string.Empty : name;

    /// <summary>Compares two names with the native collation at the user locale, ignoring case.</summary>
    internal static int Compare(string left, string right)
    {
        int result = CompareStringW(LocaleUserDefault, NormIgnoreCase, left, left.Length, right, right.Length);
        if (result == 0)
        {
            // The native call fails only for an invalid locale or flag. The managed fallback keeps
            // the sort total so a list is never left half sorted.
            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }

        return result - CompareEqual;
    }

    internal static IComparer<string> Comparer { get; } = System.Collections.Generic.Comparer<string>.Create(Compare);

    [LibraryImport("kernel32.dll", EntryPoint = "CompareStringW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CompareStringW(uint locale, uint flags, string left, int leftLength, string right, int rightLength);
}
