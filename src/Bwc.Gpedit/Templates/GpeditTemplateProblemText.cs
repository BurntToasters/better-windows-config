using Bwc.Gpedit.Policy;
using Bwc.Policy.Admx;

namespace Bwc.Gpedit.Templates;

/// <summary>
/// The user wording of a template problem. The engine reports a code and never formats text, so the editor
/// picks one sentence of Templates.resw for each code: the key is <c>TemplateProblem</c> plus the code name (a store
/// problem and an unexpected error have their own keys). The code stays visible next to the sentence as a detail
/// (MSG-031 to MSG-033).
/// </summary>
internal static class GpeditTemplateProblemText
{
    private const string KeyPrefix = "TemplateProblem";

    /// <summary>The resource key of the sentence for a problem of the load list.</summary>
    internal static string KeyFor(GpeditProblem problem) =>
        problem.Kind switch
        {
            GpeditProblemKind.TemplateFailure => KeyPrefix + problem.Detail,
            GpeditProblemKind.TemplateStore => KeyPrefix + "Store",
            _ => KeyPrefix + "LoadException",
        };

    /// <summary>The sentence for a problem of the load list. A code with no entry gives the key name.</summary>
    internal static string SentenceFor(GpeditProblem problem) => GpeditStrings.Templates(KeyFor(problem));

    /// <summary>The sentence for a loader code that an import refusal carries.</summary>
    internal static string SentenceFor(TemplateFailureCode code) => GpeditStrings.Templates(KeyPrefix + code);
}
