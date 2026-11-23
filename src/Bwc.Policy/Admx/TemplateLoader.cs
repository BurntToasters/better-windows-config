using System.Globalization;
using System.Xml.Linq;

namespace Bwc.Policy.Admx;

/// <summary>
/// Loads ADMX and ADML templates from one or more folders into one linked <see cref="TemplateSet"/>. A bad template
/// is a failure in the result, never an exception. The only exception that leaves the loader is
/// <see cref="OperationCanceledException"/>.
/// </summary>
public static class TemplateLoader
{
    /// <summary>Loads every ADMX in the folders. Earlier folders win when two files define one namespace.</summary>
    /// <param name="folders">The source folders, such as the system folder and then the import store.</param>
    /// <param name="uiCulture">
    /// The UI culture name. Each template's ADML comes from the first folder of its language chain that holds it
    /// (docs/failure-modes/policy-admx.md, section 52). A null or invalid name, or no folder of the chain, falls to en-US.
    /// </param>
    /// <param name="cancellationToken">Checked once per file.</param>
    /// <returns>The linked set, with every problem listed in <see cref="TemplateSet.Failures"/>.</returns>
    public static TemplateSet Load(
        IEnumerable<string> folders, string? uiCulture = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var culture = NormalizeCulture(uiCulture);
        var sink = new FailureSink();
        var sources = new List<SourceWork>();
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sources.Add(PrepareSource(folder, sources.Count, culture, sink));
        }

        var work = sources.SelectMany(s => s.Items).ToArray();
        var results = new FileResult[work.Length];
        RunParallel(work, results, cancellationToken);

        var parsed = new List<ParsedAdmx>();
        var next = 0;
        foreach (var source in sources)
        {
            sink.Items.AddRange(source.Failures);
            for (var i = 0; i < source.Items.Count; i++, next++)
            {
                sink.Items.AddRange(results[next].Failures);
                if (results[next].Parsed is { } file)
                {
                    parsed.Add(file);
                }
            }
        }

        return TemplateLinker.Link(culture, parsed, sink);
    }

    private static string NormalizeCulture(string? uiCulture)
    {
        var culture = uiCulture?.Trim();
        if (string.IsNullOrEmpty(culture)
            || culture is "." or ".."
            || culture.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return AdmlLanguageChain.Fallback;
        }

        return culture;
    }

    private static void RunParallel(WorkItem[] work, FileResult[] results, CancellationToken token)
    {
        var options = new ParallelOptions
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8),
        };
        try
        {
            Parallel.For(0, work.Length, options, i =>
            {
                token.ThrowIfCancellationRequested();
                results[i] = LoadFile(work[i]);
            });
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            throw new OperationCanceledException(token);
        }
    }

    private static SourceWork PrepareSource(string folder, int index, string culture, FailureSink sink)
    {
        var failures = new FailureSink();
        var items = new List<WorkItem>();
        var source = new SourceWork(failures.Items, items);
        string full;
        try
        {
            full = Path.GetFullPath(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            failures.Add(TemplateFailureCode.FolderNotFound, folder);
            return source;
        }

        if (!Directory.Exists(full))
        {
            failures.Add(TemplateFailureCode.FolderNotFound, full);
            return source;
        }

        if (SafeXml.IsReparsePointPath(full))
        {
            failures.Add(TemplateFailureCode.ReparsePoint, full);
            return source;
        }

        var languageDirs = new List<AdmlLanguageDir>();
        foreach (var language in AdmlLanguageChain.Names(culture))
        {
            var dir = UsableLanguageFolder(Path.Combine(full, language), failures);
            if (dir is not null)
            {
                languageDirs.Add(new AdmlLanguageDir(language, dir));
            }
        }

        var admxFiles = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(full))
            {
                if (path.EndsWith(".admx", StringComparison.OrdinalIgnoreCase))
                {
                    admxFiles.Add(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add(TemplateFailureCode.Unreadable, full, "0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture));
            return source;
        }

        admxFiles.Sort(CompareFileNames);
        foreach (var path in admxFiles)
        {
            items.Add(new WorkItem(
                path, index, languageDirs, Path.Combine(full, AdmlLanguageChain.Fallback, Path.GetFileNameWithoutExtension(path) + ".adml")));
        }

        return source;
    }

    private static int CompareFileNames(string left, string right)
    {
        var byName = StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(left), Path.GetFileName(right));
        return byName != 0 ? byName : StringComparer.Ordinal.Compare(left, right);
    }

    private static string? UsableLanguageFolder(string path, FailureSink failures)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }

        if (SafeXml.IsReparsePointPath(path))
        {
            failures.Add(TemplateFailureCode.ReparsePoint, path);
            return null;
        }

        return path;
    }

    private static FileResult LoadFile(WorkItem item)
    {
        var sink = new FailureSink(AdmxLimits.MaxFailuresPerFile);
        try
        {
            return new FileResult(ReadFile(item, sink), sink.Items);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sink.Add(TemplateFailureCode.InternalError, item.AdmxPath, ex.GetType().Name);
            return new FileResult(null, sink.Items);
        }
    }

    private static ParsedAdmx? ReadFile(WorkItem item, FailureSink sink)
    {
        var admxRoot = SafeXml.Load(item.AdmxPath, sink);
        if (admxRoot is null)
        {
            return null;
        }

        var admxContext = new ParseContext(item.AdmxPath, sink);
        var file = AdmxParser.ReadHeader(admxRoot, item.AdmxPath, admxContext);
        if (file is null)
        {
            return null;
        }

        var baseName = Path.GetFileNameWithoutExtension(item.AdmxPath) + ".adml";
        var found = FindAdml(item, baseName);
        if (found is null)
        {
            sink.Add(TemplateFailureCode.MissingAdml, item.ExpectedAdmlPath, item.AdmxPath);
            return null;
        }

        var (admlPath, language) = found.Value;
        var admlRoot = SafeXml.Load(admlPath, sink);
        if (admlRoot is null)
        {
            return null;
        }

        var adml = AdmlParser.Parse(admlRoot, new ParseContext(admlPath, sink));
        if (adml is null)
        {
            return null;
        }

        file.AdmlPath = admlPath;
        file.Culture = language;
        file.SourceIndex = item.SourceIndex;
        file.DisplayName = adml.DisplayName;
        file.Description = adml.Description;
        var parsed = AdmxParser.ParseBody(admxRoot, file, adml, admxContext);

        // Classic refuses a file it cannot load and shows none of its content, so a file with any failure recorded
        // while it was read adds nothing. The failures stay in the result. Dangling references (a using, a category or
        // a supported-on that names nothing) are found later, by the linker, and do not reject a file: shipped
        // templates carry them and classic loads those.
        return sink.Items.Count > 0 ? null : parsed;
    }

    /// <summary>The first ADML of the chain for the template, with the language folder it came from.</summary>
    private static (string Path, string Language)? FindAdml(WorkItem item, string baseName)
    {
        foreach (var dir in item.LanguageDirs)
        {
            var candidate = Path.Combine(dir.Path, baseName);
            if (AdmlExists(candidate))
            {
                return (candidate, dir.Language);
            }
        }

        return null;
    }

    private static bool AdmlExists(string path) => File.Exists(path) || SafeXml.IsReparsePointPath(path);

    private sealed record SourceWork(List<TemplateFailure> Failures, List<WorkItem> Items);

    /// <summary>A language folder of the chain that exists and is usable, with its name as the chain gives it.</summary>
    private sealed record AdmlLanguageDir(string Language, string Path);

    private sealed record WorkItem(string AdmxPath, int SourceIndex, List<AdmlLanguageDir> LanguageDirs, string ExpectedAdmlPath);

    private sealed record FileResult(ParsedAdmx? Parsed, List<TemplateFailure> Failures);
}
