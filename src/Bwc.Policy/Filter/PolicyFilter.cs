using Bwc.Policy.Admx;
using Bwc.Policy.Model;

namespace Bwc.Policy.Filter;

/// <summary>One policy as the filter sees it: the definition, its state in one hive and its comment.</summary>
/// <param name="Policy">The definition.</param>
/// <param name="State">The state in the hive being listed, or null when it could not be read.</param>
/// <param name="Comment">The comment of the policy in that hive, or null when it has none or could not be read.</param>
public readonly record struct FilterSubject(PolicyDefinition Policy, PolicyStateSnapshot? State, string? Comment);

/// <summary>How many subjects a filter shows out of how many it was given (Oracle admtmpl.dll.mui:3124 to 3126).</summary>
/// <param name="Shown">The subjects that match.</param>
/// <param name="Total">All subjects.</param>
public readonly record struct FilterCount(int Shown, int Total);

/// <summary>The subjects that match and the number given.</summary>
/// <param name="Shown">The matching subjects in the order given.</param>
/// <param name="Total">The number of subjects given.</param>
public sealed record FilterResult(IReadOnlyList<FilterSubject> Shown, int Total);

/// <summary>
/// The predicate of the Filter Options dialog. Pure: it reads the subject and the options it was built from. The rules
/// the resources do not settle are in <see cref="FilterRules"/>.
/// </summary>
public sealed class PolicyFilter
{
    private const int CancellationStride = 256;

    private readonly FilterOptions options;
    private readonly IReadOnlyList<string> words;
    private readonly string phrase;
    private readonly HashSet<string> platforms;

    private PolicyFilter(FilterOptions options)
    {
        this.options = options;
        words = FilterRules.SplitWords(options.Keyword.Text);
        phrase = FilterRules.CollapseWhiteSpace(options.Keyword.Text ?? string.Empty);
        platforms = new HashSet<string>(options.Requirements.PlatformKeys ?? [], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The options this filter was built from.</summary>
    public FilterOptions Options => options;

    /// <summary>Builds a filter. Returns false and the problems when the options cannot be applied.</summary>
    public static bool TryCreate(FilterOptions options, out PolicyFilter? filter, out IReadOnlyList<FilterProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(options);
        problems = options.Validate();
        filter = problems.Count == 0 ? new PolicyFilter(options with { Requirements = Copy(options.Requirements) }) : null;
        return filter is not null;
    }

    /// <summary>True when the subject passes every enabled part.</summary>
    public bool Matches(FilterSubject subject)
    {
        var policy = subject.Policy ?? throw new ArgumentNullException(nameof(subject));
        return MatchesManaged(policy)
            && MatchesConfigured(subject.State)
            && MatchesCommented(subject.Comment)
            && MatchesKeyword(policy, subject.Comment)
            && MatchesRequirements(policy);
    }

    /// <summary>Filters the subjects. Checks the token once per 256 subjects and returns nothing when cancelled.</summary>
    public FilterResult Apply(IEnumerable<FilterSubject> subjects, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        var shown = new List<FilterSubject>();
        var total = 0;
        foreach (var subject in subjects)
        {
            if (total++ % CancellationStride == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (Matches(subject))
            {
                shown.Add(subject);
            }
        }

        return new FilterResult(shown, total);
    }

    /// <summary>Counts the subjects that match, for a status line.</summary>
    public FilterCount Count(IEnumerable<FilterSubject> subjects, CancellationToken cancellationToken = default)
    {
        var result = Apply(subjects, cancellationToken);
        return new FilterCount(result.Shown.Count, result.Total);
    }

    private static RequirementsFilter Copy(RequirementsFilter requirements) =>
        requirements with { PlatformKeys = (requirements.PlatformKeys ?? []).ToArray() };

    private bool MatchesManaged(PolicyDefinition policy) =>
        options.Managed == FilterTriState.Any || FilterRules.IsManagedKey(policy.Key) == (options.Managed == FilterTriState.Yes);

    private bool MatchesConfigured(PolicyStateSnapshot? state)
    {
        if (options.Configured == FilterTriState.Any)
        {
            return true;
        }

        if (state is null)
        {
            return !FilterRules.UnknownStateMatchesOnlyAny;
        }

        return (state.State != PolicyState.NotConfigured) == (options.Configured == FilterTriState.Yes);
    }

    private bool MatchesCommented(string? comment) =>
        options.Commented == FilterTriState.Any || FilterRules.HasComment(comment) == (options.Commented == FilterTriState.Yes);

    private bool MatchesKeyword(PolicyDefinition policy, string? comment)
    {
        var keyword = options.Keyword;
        if (!keyword.Enabled)
        {
            return true;
        }

        var texts = new List<string>(3);
        AddText(texts, keyword.InTitle, policy.DisplayName);
        AddText(texts, keyword.InHelpText, policy.ExplainText);
        AddText(texts, keyword.InComment, comment);
        if (texts.Count == 0)
        {
            return false;
        }

        switch (keyword.Mode)
        {
            case KeywordMatchMode.Exact:
                return texts.Any(ExactContains);
            case KeywordMatchMode.All:
                return FilterRules.KeywordAllMayUseDifferentTexts
                    ? words.All(w => texts.Any(t => Contains(t, w)))
                    : texts.Any(t => words.All(w => Contains(t, w)));
            default:
                return words.Any(w => texts.Any(t => Contains(t, w)));
        }
    }

    private bool ExactContains(string text) =>
        FilterRules.KeywordExactCollapsesWhiteSpace
            ? FilterRules.CollapseWhiteSpace(text).Contains(phrase, FilterRules.Comparison)
            : text.Contains(options.Keyword.Text.Trim(), FilterRules.Comparison);

    private static bool Contains(string text, string word)
    {
        if (FilterRules.KeywordMatchesSubstring)
        {
            return text.Contains(word, FilterRules.Comparison);
        }

        var from = 0;
        while (from <= text.Length - word.Length)
        {
            var at = text.IndexOf(word, from, FilterRules.Comparison);
            if (at < 0)
            {
                return false;
            }

            var before = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            var after = at + word.Length >= text.Length || !char.IsLetterOrDigit(text[at + word.Length]);
            if (before && after)
            {
                return true;
            }

            from = at + 1;
        }

        return false;
    }

    private static void AddText(List<string> texts, bool within, string? text)
    {
        if (within && !string.IsNullOrEmpty(text))
        {
            texts.Add(text);
        }
    }

    private bool MatchesRequirements(PolicyDefinition policy)
    {
        if (!options.Requirements.Enabled)
        {
            return true;
        }

        if (policy.SupportedOn is null)
        {
            return !FilterRules.NoSupportedOnFailsRequirements;
        }

        var key = PlatformList.KeyOf(policy.SupportedOn);
        return options.Requirements.Mode == PlatformMatchMode.Any
            ? platforms.Contains(key)
            : !FilterRules.RequirementsAllNeedsEverySelected ? platforms.Contains(key) : platforms.All(p => string.Equals(p, key, StringComparison.OrdinalIgnoreCase));
    }
}
