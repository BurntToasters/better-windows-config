using System.Runtime.InteropServices;

namespace Bwc.Registry;

/// <summary>
/// The Windows NLS comparison classic regedit uses for Find matching and for the display order
/// of keys and values. It calls <c>CompareStringEx</c> with <c>NORM_IGNORECASE</c> at the user
/// default locale, as <c>lstrcmpiW</c> does. The managed current-culture comparer is not used.
/// </summary>
internal static partial class RegistryFindCollation
{
    private const uint NormIgnoreCase = 0x00000001;
    private const int CompareEqual = 2;

    /// <summary>The sort comparer for key and value names.</summary>
    internal static IComparer<string> Comparer { get; } =
        System.Collections.Generic.Comparer<string>.Create(Compare);

    /// <summary>Compares two strings with the classic find collation.</summary>
    internal static int Compare(string left, string right) =>
        CompareCore(left, 0, left.Length, right, 0, right.Length);

    /// <summary>True when the two strings are equal under the classic find collation.</summary>
    internal static bool Equals(string left, string right) => Compare(left, right) == 0;

    /// <summary>
    /// True when <paramref name="needle"/> occurs in <paramref name="text"/>. Classic compares
    /// a needle-length window at every UTF-16 offset, so a match may cross an embedded NUL.
    /// </summary>
    internal static bool Contains(string text, string needle)
    {
        if (needle.Length == 0)
        {
            return true;
        }

        if (text.Length < needle.Length)
        {
            return false;
        }

        for (int index = 0; index <= text.Length - needle.Length; index += 1)
        {
            if (CompareCore(text, index, needle.Length, needle, 0, needle.Length) == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static unsafe int CompareCore(
        string first,
        int firstIndex,
        int firstLength,
        string second,
        int secondIndex,
        int secondLength)
    {
        fixed (char* firstPointer = first)
        fixed (char* secondPointer = second)
        {
            int result = CompareStringEx(
                null,
                NormIgnoreCase,
                firstPointer + firstIndex,
                firstLength,
                secondPointer + secondIndex,
                secondLength,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (result == 0)
            {
                // The native call fails only for an invalid locale or flag. Keep the walk and
                // sort total; ordinal ignore case is not the semantic model.
                string firstSlice = first.Substring(firstIndex, firstLength);
                string secondSlice = second.Substring(secondIndex, secondLength);
                return Math.Sign(string.Compare(
                    firstSlice,
                    secondSlice,
                    StringComparison.OrdinalIgnoreCase));
            }

            return result - CompareEqual;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CompareStringEx")]
    private static unsafe partial int CompareStringEx(
        char* localeName,
        uint flags,
        char* first,
        int firstLength,
        char* second,
        int secondLength,
        IntPtr versionInformation,
        IntPtr reserved,
        IntPtr parameter);
}
