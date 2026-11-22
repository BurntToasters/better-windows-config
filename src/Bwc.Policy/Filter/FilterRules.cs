using System.Text;

namespace Bwc.Policy.Filter;

/// <summary>
/// The filter rules that the classic resources do not settle. Each member is one decision; the capture case of group F
/// that settles it is named in its comment, and a member with no case points at a row of the Gaps section of
/// docs/parity/gpedit-audit.md. docs/failure-modes/policy-filter.md has the reasoning for each.
/// </summary>
public static class FilterRules
{
    /// <summary>The longest keyword text that <see cref="FilterOptions.Validate"/> accepts. A limit of ours.</summary>
    public const int MaxKeywordLength = 4096;

    /// <summary>[Rule: KeywordIgnoresCase] Keyword matching ignores case.</summary>
    public static readonly bool KeywordIgnoresCase = true;

    /// <summary>[Rule: KeywordMatchesSubstring] A word matches inside a longer word.</summary>
    public static readonly bool KeywordMatchesSubstring = true;

    /// <summary>
    /// [Rule: KeywordAllMayUseDifferentTexts] With All, the words may not be found in different ticked texts: every word
    /// must be in one text. F/Filter_Options op 54 (words "policy sort" in title and help of the Sort cases) hid every
    /// policy, so a word of the title plus a word of the help does not match.
    /// </summary>
    public static readonly bool KeywordAllMayUseDifferentTexts = false;

    /// <summary>[Rule: KeywordExactCollapsesWhiteSpace] With Exact, runs of white space count as one space.</summary>
    public static readonly bool KeywordExactCollapsesWhiteSpace = true;

    /// <summary>[Rule: BlankCommentIsNoComment] A comment of only white space counts as no comment.</summary>
    public static readonly bool BlankCommentIsNoComment = true;

    /// <summary>[Rule: UnknownStateMatchesOnlyAny] A policy whose state is unreadable matches only Configured: Any.</summary>
    public static readonly bool UnknownStateMatchesOnlyAny = true;

    /// <summary>[Rule: NoSupportedOnFailsRequirements] With requirements on, a policy with no supported-on is out.</summary>
    public static readonly bool NoSupportedOnFailsRequirements = true;

    /// <summary>[Rule: RequirementsAllNeedsEverySelected] With All, every selected platform must be the policy one.</summary>
    public static readonly bool RequirementsAllNeedsEverySelected = true;

    /// <summary>[Rule: EmptyCategoryStaysVisible] A category with no match stays in the tree. The UI reads this.</summary>
    public static readonly bool EmptyCategoryStaysVisible = true;

    /// <summary>[Rule: AllSettingsIncludesUncategorized] All Settings lists a policy that has no category.</summary>
    public static readonly bool AllSettingsIncludesUncategorized = true;

    /// <summary>
    /// [Rule: ManagedKeyRoots] A policy is managed when its key is one of these or below one. A key is compared ignoring
    /// case, on a key boundary.
    /// </summary>
    public static readonly IReadOnlyList<string> ManagedKeyRoots =
    [
        @"Software\Policies",
        @"Software\Microsoft\Windows\CurrentVersion\Policies",
    ];

    /// <summary>The comparison for keyword matching.</summary>
    internal static StringComparison Comparison => KeywordIgnoresCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>[Rule: KeywordWordsSplitOnWhiteSpace] The words of a keyword text, split on white space.</summary>
    internal static IReadOnlyList<string> SplitWords(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The text with each run of white space replaced by one space and the ends trimmed.</summary>
    internal static string CollapseWhiteSpace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>True when the policy key is under a managed root.</summary>
    internal static bool IsManagedKey(string? key)
    {
        var trimmed = key?.Trim('\\');
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        foreach (var root in ManagedKeyRoots)
        {
            if (trimmed.Equals(root, StringComparison.OrdinalIgnoreCase)
                || (trimmed.Length > root.Length
                    && trimmed.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    && trimmed[root.Length] == '\\'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the comment counts as a comment.</summary>
    internal static bool HasComment(string? comment) =>
        !string.IsNullOrEmpty(comment) && (!BlankCommentIsNoComment || !string.IsNullOrWhiteSpace(comment));
}
