namespace Bwc.Policy.Filter;

/// <summary>A Yes, No or Any choice of a combo box of the dialog (Oracle admtmpl.dll.mui:3000 to 3002).</summary>
public enum FilterTriState
{
    /// <summary>The part does not filter.</summary>
    Any,

    /// <summary>Only policies for which the fact is true.</summary>
    Yes,

    /// <summary>Only policies for which the fact is false.</summary>
    No,
}

/// <summary>How the words of the keyword filter combine (Oracle admtmpl.dll.mui:3003 to 3005).</summary>
public enum KeywordMatchMode
{
    /// <summary>At least one word is found.</summary>
    Any,

    /// <summary>Every word is found.</summary>
    All,

    /// <summary>The whole text is found as one phrase.</summary>
    Exact,
}

/// <summary>How the selected platforms combine (Oracle admtmpl.dll.mui:3119 and 3120).</summary>
public enum PlatformMatchMode
{
    /// <summary>The policy matches at least one selected platform.</summary>
    Any,

    /// <summary>The policy matches every selected platform.</summary>
    All,
}

/// <summary>A reason the options cannot be applied. The dialog maps each to its message.</summary>
public enum FilterProblem
{
    /// <summary>The keyword filter is on and the text has no word (Oracle admtmpl.dll.mui:3122).</summary>
    KeywordTextMissing,

    /// <summary>The keyword filter is on and no Within box is ticked (Oracle admtmpl.dll.mui:3123).</summary>
    KeywordWithinMissing,

    /// <summary>The keyword text is longer than <see cref="FilterRules.MaxKeywordLength"/>.</summary>
    KeywordTooLong,

    /// <summary>The requirements filter is on and no platform is selected (Oracle admtmpl.dll.mui:3121).</summary>
    PlatformsMissing,
}

/// <summary>The keyword part of the options (dialog controls 3504 to 3508 and 3517).</summary>
/// <param name="Enabled">Whether the part filters at all.</param>
/// <param name="Text">The words, as typed.</param>
/// <param name="Mode">Any, All or Exact.</param>
/// <param name="InTitle">Search the policy setting title.</param>
/// <param name="InHelpText">Search the help text.</param>
/// <param name="InComment">Search the comment.</param>
public sealed record KeywordFilter(bool Enabled, string Text, KeywordMatchMode Mode, bool InTitle, bool InHelpText, bool InComment)
{
    /// <summary>A disabled keyword part.</summary>
    public static KeywordFilter Off { get; } = new(false, string.Empty, KeywordMatchMode.Any, true, true, true);
}

/// <summary>The requirements part of the options (dialog controls 3509 to 3513).</summary>
/// <param name="Enabled">Whether the part filters at all.</param>
/// <param name="PlatformKeys">The selected platform keys (<see cref="PlatformList.KeyOf"/>).</param>
/// <param name="Mode">Any or All.</param>
public sealed record RequirementsFilter(bool Enabled, IReadOnlyList<string> PlatformKeys, PlatformMatchMode Mode)
{
    /// <summary>A disabled requirements part.</summary>
    public static RequirementsFilter Off { get; } = new(false, [], PlatformMatchMode.Any);
}

/// <summary>The whole Filter Options dialog as a value.</summary>
/// <param name="Managed">Managed: Any, Yes or No.</param>
/// <param name="Configured">Configured: Any, Yes or No.</param>
/// <param name="Commented">Commented: Any, Yes or No.</param>
/// <param name="Keyword">The keyword part.</param>
/// <param name="Requirements">The requirements part.</param>
public sealed record FilterOptions(
    FilterTriState Managed,
    FilterTriState Configured,
    FilterTriState Commented,
    KeywordFilter Keyword,
    RequirementsFilter Requirements)
{
    /// <summary>Options that pass every policy.</summary>
    public static FilterOptions None { get; } =
        new(FilterTriState.Any, FilterTriState.Any, FilterTriState.Any, KeywordFilter.Off, RequirementsFilter.Off);

    /// <summary>The problems that stop OK in the dialog. Empty when the options can be applied.</summary>
    public IReadOnlyList<FilterProblem> Validate()
    {
        var problems = new List<FilterProblem>();
        if (Keyword.Enabled)
        {
            if (Keyword.Text is { Length: > FilterRules.MaxKeywordLength })
            {
                problems.Add(FilterProblem.KeywordTooLong);
            }
            else if (FilterRules.SplitWords(Keyword.Text).Count == 0)
            {
                problems.Add(FilterProblem.KeywordTextMissing);
            }

            if (!Keyword.InTitle && !Keyword.InHelpText && !Keyword.InComment)
            {
                problems.Add(FilterProblem.KeywordWithinMissing);
            }
        }

        if (Requirements.Enabled && (Requirements.PlatformKeys is null || Requirements.PlatformKeys.Count == 0))
        {
            problems.Add(FilterProblem.PlatformsMissing);
        }

        return problems;
    }
}
