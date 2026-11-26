using System.Globalization;
using System.Text;
using Bwc.Policy.Admx;
using Bwc.Policy.Store;
using Bwc.Gpedit.Policy;

namespace Bwc.Gpedit.Templates;

/// <summary>The state of one file of the import store in the Add/Remove Templates list.</summary>
internal enum TemplateRowStatus
{
    /// <summary>The template loaded and its policies are in the tree.</summary>
    Loaded,

    /// <summary>A system template of the same namespace hides it.</summary>
    Shadowed,

    /// <summary>The file did not load. It is listed so that it can be removed.</summary>
    NotLoaded,
}

/// <summary>One row of the imported templates list: file name, namespace (empty when it did not load), status.</summary>
internal sealed record TemplateRowInfo(string FileName, string Namespace, TemplateRowStatus Status);

/// <summary>What one Add or Remove did: the problems to show (empty on success) and whether the store changed.</summary>
internal sealed record TemplateOperationResult(IReadOnlyList<TemplateImportProblem> Problems, bool Changed);

/// <summary>
/// The Add/Remove Templates dialog's view of the import store, over the engine (<see cref="TemplateSources"/> and
/// <see cref="TemplateImport"/>). It keeps no copy of the engine's checks: an Add is <see cref="TemplateImport.Import"/>, a
/// Remove is <see cref="TemplateImport.Remove"/> for a file that loaded and <see cref="TemplateImport.RemoveFile"/> for
/// one that did not. All writes go through the engine's template store writer. The calls block, so the dialog runs them
/// off the UI thread (docs/failure-modes/gpedit-templates.md, sections 3, 5, 6, 8 and 9).
/// </summary>
internal sealed class GpeditTemplateStore
{
    private readonly TemplateSources _sources = GpeditStorePolicy.CreateTemplateSources();
    private readonly string _culture = CultureInfo.CurrentUICulture.Name;

    /// <summary>
    /// The files of the import store, one row each and ordered by file name (ordinal, ignoring case). The system folder is
    /// loaded as well, because a shadowed import is a state of the pair. A store that cannot be used lists nothing.
    /// </summary>
    internal IReadOnlyList<TemplateRowInfo> ReadRows()
    {
#if BWC_E2E
        if (int.TryParse(
            Environment.GetEnvironmentVariable("BWC_E2E_GPEDIT_TEMPLATE_READ_DELAY_MS"),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int delayMilliseconds)
            && delayMilliseconds is > 0 and <= 15000)
        {
            Thread.Sleep(delayMilliseconds);
        }
#endif

        TemplateSourcesResult loaded = _sources.Load(_culture);
        if (loaded.Store != TemplateStoreStatus.Ready)
        {
            return [];
        }

        var byName = new Dictionary<string, TemplateRowInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (TemplateFile file in loaded.ImportedFiles)
        {
            string name = Path.GetFileName(file.AdmxPath);
            byName[name] = new TemplateRowInfo(name, file.Namespace, TemplateRowStatus.Loaded);
        }

        foreach (ShadowedTemplate shadowed in loaded.Shadowed)
        {
            string name = Path.GetFileName(shadowed.Path);
            byName[name] = new TemplateRowInfo(name, shadowed.Namespace, TemplateRowStatus.Shadowed);
        }

        foreach (string path in Directory.EnumerateFiles(_sources.StoreFolder))
        {
            string name = Path.GetFileName(path);
            if (name.EndsWith(TemplateFileNames.AdmxExtension, StringComparison.OrdinalIgnoreCase) && !byName.ContainsKey(name))
            {
                byName[name] = new TemplateRowInfo(name, string.Empty, TemplateRowStatus.NotLoaded);
            }
        }

        return byName.Values.OrderBy(r => r.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Checks the chosen file and copies it into the store. A refusal writes nothing.</summary>
    internal TemplateOperationResult Add(string path)
    {
        try
        {
            TemplateImportResult result = new TemplateImport(_sources).Import(path, _culture);
            return new TemplateOperationResult(result.Problems, result.Imported);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return Unexpected(path, ex);
        }
    }

    /// <summary>Removes the template of a row. A removal the engine refuses (a dependent uses it) changes nothing.</summary>
    internal TemplateOperationResult Remove(TemplateRowInfo row)
    {
        try
        {
            var importer = new TemplateImport(_sources);
            TemplateRemoveResult result = row.Status == TemplateRowStatus.Loaded
                ? importer.Remove(row.Namespace, _culture)
                : importer.RemoveFile(row.FileName, _culture);
            return new TemplateOperationResult(result.Problems, result.Removed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return Unexpected(row.FileName, ex);
        }
    }

    /// <summary>
    /// The text of the details box: one line per problem, <c>&lt;ProblemCode&gt;[/&lt;load code or store reason&gt;] &lt;file
    /// name&gt; &lt;detail&gt;</c>, and for a loader refusal the sentence for the loader code after it. The codes are the
    /// engine's names.
    /// </summary>
    internal static string FormatProblems(IEnumerable<TemplateImportProblem> problems)
    {
        var text = new StringBuilder();
        foreach (TemplateImportProblem problem in problems)
        {
            if (text.Length > 0)
            {
                _ = text.Append("\r\n");
            }

            _ = text.Append(problem.Code);
            if (problem.LoadCode is { } load)
            {
                _ = text.Append('/').Append(load);
            }
            else if (problem.StoreFailure is { } store)
            {
                _ = text.Append('/').Append(store);
            }

            string file = FileNameOf(problem.Path);
            if (file.Length > 0)
            {
                _ = text.Append(' ').Append(file);
            }

            if (problem.Detail.Length > 0)
            {
                _ = text.Append(' ').Append(problem.Detail);
            }

            // A refusal by the loader gets the sentence for its code after the code line (MSG-031 to MSG-033).
            if (problem.LoadCode is { } loadCode)
            {
                _ = text.Append("  ").Append(GpeditTemplateProblemText.SentenceFor(loadCode));
            }
        }

        return text.ToString();
    }

    /// <summary>The last segment of a path, also for a folder with a trailing separator. Empty for no path.</summary>
    internal static string FileNameOf(string path) =>
        string.IsNullOrEmpty(path) ? string.Empty : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static TemplateOperationResult Unexpected(string path, Exception ex) =>
        new([new TemplateImportProblem(TemplateImportProblemCode.WriteFailed, path, ex.GetType().Name)], false);
}
