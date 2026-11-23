using System.Globalization;
using Bwc.Policy.Admx;

namespace Bwc.Policy.Store;

/// <summary>
/// Checks a chosen ADMX (with its ADML for the UI culture, else en-US), copies the pair into the import store through
/// <see cref="TemplateStoreWriter"/>, and removes an imported template. The check loads the file through the hardened
/// <see cref="TemplateLoader"/> together with the system folder and the store, so a using reference resolves the way it
/// will after the copy. A refusal is a list of typed problems, never an exception; only cancellation throws.
/// Classic <c>.adm</c> files are refused (a declared difference). docs/failure-modes/template-store.md, C to E.
/// </summary>
public sealed class TemplateImport
{
    private const string FallbackCulture = "en-US";

    /// <summary>The most ADMX files the folder of a chosen file may hold, itself included (template-store.md, I6).</summary>
    public const int MaxSourceFolderAdmxFiles = 1000;

    private readonly TemplateSources sources;
    private readonly Action<TemplateImportStage>? observer;

    /// <summary>Creates an importer over the two sources.</summary>
    public TemplateImport(TemplateSources sources)
        : this(sources, null)
    {
    }

    /// <summary>
    /// Test seam (docs/failure-modes/template-store.md, I8). The observer gets AdmxRead right after the chosen
    /// ADMX was read and Loaded right after the templates loaded, once each per check. It changes no behavior.
    /// </summary>
    internal TemplateImport(TemplateSources sources, Action<TemplateImportStage>? observer)
    {
        ArgumentNullException.ThrowIfNull(sources);
        this.sources = sources;
        this.observer = observer;
    }

    /// <summary>Checks a template without writing anything.</summary>
    /// <param name="admxPath">The chosen ADMX file.</param>
    /// <param name="uiCulture">The UI culture name, or null for en-US.</param>
    /// <param name="cancellationToken">Checked while the templates load.</param>
    public TemplateImportCheck Check(string admxPath, string? uiCulture = null, CancellationToken cancellationToken = default)
    {
        var problems = new List<TemplateImportProblem>();
        var full = ReadName(admxPath, problems);
        if (full is null)
        {
            return new TemplateImportCheck(problems);
        }

        var name = Path.GetFileName(full);
        var admxName = Path.GetFileNameWithoutExtension(name) + TemplateFileNames.AdmxExtension;
        var tooLarge = CheckFolderCap(Path.GetDirectoryName(full)!);
        if (tooLarge is not null)
        {
            problems.Add(tooLarge);
            return new TemplateImportCheck(problems);
        }

        var admx = ReadBounded(full, problems);
        var (status, detail) = sources.InspectStore();
        if (status is not (TemplateStoreStatus.Missing or TemplateStoreStatus.Ready))
        {
            problems.Add(new TemplateImportProblem(TemplateImportProblemCode.StoreUnavailable, sources.StoreFolder, status + ": " + detail));
        }

        if (problems.Count > 0 || admx is null)
        {
            return new TemplateImportCheck(problems);
        }

        observer?.Invoke(TemplateImportStage.AdmxRead);
        return CheckLoaded(full, admxName, admx, uiCulture, status == TemplateStoreStatus.Ready, problems, cancellationToken);
    }

    /// <summary>Checks the template, copies it into the store when it passes, and proves that it loads from there.</summary>
    /// <param name="admxPath">The chosen ADMX file.</param>
    /// <param name="uiCulture">The UI culture name, or null for en-US.</param>
    /// <param name="cancellationToken">Checked while the templates load and before the first write.</param>
    public TemplateImportResult Import(string admxPath, string? uiCulture = null, CancellationToken cancellationToken = default)
    {
        var check = Check(admxPath, uiCulture, cancellationToken);
        if (!check.CanImport)
        {
            return new TemplateImportResult(false, check.Problems, check.Namespace);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var admxFile = check.AdmxFileName!;
        var culture = check.Culture!;
        TemplateStoreWriter writer;
        try
        {
            writer = new TemplateStoreWriter(sources.StoreFolder, sources.SystemFolder, sources.Acl);
            writer.WriteTemplate(admxFile, check.Admx!, culture, check.Adml!);
        }
        catch (TemplateStoreException e)
        {
            return new TemplateImportResult(false, [FromStoreException(e)], check.Namespace);
        }

        var admxPathInStore = Path.Combine(writer.StoreRoot, admxFile);
        var admlPathInStore = Path.Combine(writer.StoreRoot, culture, TemplateFileNames.AdmlNameOf(admxFile));
        var verify = Verify(check.Namespace!, admxPathInStore, admlPathInStore, uiCulture, cancellationToken);
        if (verify is not null)
        {
            var rollback = new List<TemplateImportProblem> { verify };
            try
            {
                writer.RemoveTemplate(admxFile);
            }
            catch (TemplateStoreException e)
            {
                rollback.Add(FromStoreException(e));
            }

            return new TemplateImportResult(false, rollback, check.Namespace);
        }

        return new TemplateImportResult(true, [], check.Namespace, admxPathInStore, admlPathInStore);
    }

    /// <summary>Removes an imported template by its namespace.</summary>
    /// <param name="templateNamespace">The namespace of a template in the store.</param>
    /// <param name="cancellationToken">Checked while the store loads.</param>
    public TemplateRemoveResult Remove(string templateNamespace, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(templateNamespace);
        var (status, detail) = sources.InspectStore();
        var blocked = StoreProblem(status, detail);
        if (blocked is not null)
        {
            return new TemplateRemoveResult(false, [blocked], []);
        }

        var set = TemplateLoader.Load([sources.StoreFolder], null, cancellationToken);
        var file = set.FindFile(templateNamespace);
        return file is null
            ? new TemplateRemoveResult(false, [new TemplateImportProblem(TemplateImportProblemCode.NotImported, sources.StoreFolder, templateNamespace)], [])
            : RemoveFile(Path.GetFileName(file.AdmxPath), cancellationToken);
    }

    /// <summary>Removes an imported template by its ADMX file name. It works for a file that does not load.</summary>
    /// <param name="admxFileName">The file name in the store.</param>
    /// <param name="cancellationToken">Checked while the store loads.</param>
    public TemplateRemoveResult RemoveFile(string admxFileName, CancellationToken cancellationToken = default)
    {
        if (!TemplateFileNames.IsSafeAdmxFileName(admxFileName))
        {
            return new TemplateRemoveResult(false, [new TemplateImportProblem(TemplateImportProblemCode.UnsafeFileName, admxFileName ?? string.Empty)], []);
        }

        var (status, detail) = sources.InspectStore();
        var blocked = StoreProblem(status, detail);
        if (blocked is not null)
        {
            return new TemplateRemoveResult(false, [blocked], []);
        }

        var path = Path.Combine(sources.StoreFolder, admxFileName);
        if (!File.Exists(path))
        {
            return new TemplateRemoveResult(false, [new TemplateImportProblem(TemplateImportProblemCode.NotImported, path, admxFileName)], []);
        }

        var dependents = Dependents(path, cancellationToken);
        if (dependents.Count > 0)
        {
            return new TemplateRemoveResult(false, dependents, []);
        }

        var removed = new List<string>();
        try
        {
            removed.AddRange(new TemplateStoreWriter(sources.StoreFolder, sources.SystemFolder, sources.Acl).RemoveTemplate(admxFileName));
        }
        catch (TemplateStoreException e)
        {
            return new TemplateRemoveResult(!File.Exists(path), [FromStoreException(e)], removed);
        }

        return new TemplateRemoveResult(true, [], removed);
    }

    private TemplateImportCheck CheckLoaded(
        string full, string admxName, byte[] admx, string? uiCulture, bool storeLoaded, List<TemplateImportProblem> problems,
        CancellationToken cancellationToken)
    {
        var sourceFolder = Path.GetDirectoryName(full)!;
        var folders = storeLoaded
            ? new[] { sources.SystemFolder, sources.StoreFolder, sourceFolder }
            : [sources.SystemFolder, sourceFolder];
        var sourceIndex = folders.Length - 1;

        // The pair is read once more right before the load and again after it, and every read must be equal. The
        // bytes that are written are these bytes (template-store.md, I1 and I2).
        var beforeAdmx = TryRead(full);
        var (beforeAdmlPath, beforeAdml) = ReadPredictedAdml(sourceFolder, Path.GetFileNameWithoutExtension(full), uiCulture);
        var set = TemplateLoader.Load(folders, uiCulture, cancellationToken);
        observer?.Invoke(TemplateImportStage.Loaded);
        var candidate = set.Files.FirstOrDefault(f => f.SourceIndex == sourceIndex && TemplateStoreChecks.IsSame(f.AdmxPath, full));
        var stem = Path.GetFileNameWithoutExtension(full);
        foreach (var failure in set.Failures.Where(f => IsRelevant(f, full, sourceFolder, stem, uiCulture)))
        {
            problems.Add(MapFailure(failure, set));
        }

        if (candidate is null)
        {
            if (problems.Count == 0)
            {
                problems.Add(new TemplateImportProblem(TemplateImportProblemCode.LoadFailed, full));
            }

            return new TemplateImportCheck(problems);
        }

        foreach (var use in candidate.Usings)
        {
            var defined = set.FindFile(use.Namespace);
            if (defined is not null && defined.SourceIndex == sourceIndex && !ReferenceEquals(defined, candidate))
            {
                problems.Add(new TemplateImportProblem(TemplateImportProblemCode.UsingNotInstalled, full, use.Namespace));
            }
        }

        var inStore = Path.Combine(sources.StoreFolder, admxName);
        if (File.Exists(inStore) && !problems.Any(p => p.Code == TemplateImportProblemCode.NamespaceAlreadyImported))
        {
            problems.Add(new TemplateImportProblem(TemplateImportProblemCode.FileNameInUse, inStore, admxName));
        }

        var adml = problems.Count == 0 ? ReadBounded(candidate.AdmlPath, problems) : null;
        var again = problems.Count == 0 ? ReadBounded(full, problems) : null;
        if (problems.Count == 0)
        {
            if (beforeAdmx is null || !admx.AsSpan().SequenceEqual(beforeAdmx) || !admx.AsSpan().SequenceEqual(again))
            {
                problems.Add(new TemplateImportProblem(TemplateImportProblemCode.SourceChanged, full));
            }
            else if (beforeAdml is null || beforeAdmlPath is null
                || !TemplateStoreChecks.IsSame(beforeAdmlPath, candidate.AdmlPath) || !beforeAdml.AsSpan().SequenceEqual(adml))
            {
                problems.Add(new TemplateImportProblem(TemplateImportProblemCode.SourceChanged, candidate.AdmlPath));
            }
        }

        return new TemplateImportCheck(problems)
        {
            Namespace = candidate.Namespace,
            AdmxFileName = admxName,
            Culture = candidate.Culture,
            Admx = again,
            Adml = adml,
        };
    }

    private static TemplateImportProblem? CheckFolderCap(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        try
        {
            var count = 0;
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                if (path.EndsWith(TemplateFileNames.AdmxExtension, StringComparison.OrdinalIgnoreCase)
                    && ++count > MaxSourceFolderAdmxFiles)
                {
                    return new TemplateImportProblem(
                        TemplateImportProblemCode.SourceFolderTooLarge, folder, MaxSourceFolderAdmxFiles.ToString(CultureInfo.InvariantCulture));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new TemplateImportProblem(
                TemplateImportProblemCode.SourceUnreadable, folder, "0x" + e.HResult.ToString("X8", CultureInfo.InvariantCulture));
        }

        return null;
    }

    /// <summary>Reads a file the way the check does, without a problem list. Null when it cannot be read.</summary>
    private static byte[]? TryRead(string path) => ReadBounded(path, []);

    /// <summary>The ADML the loader picks for the chosen ADMX (the culture folder, else en-US) and its bytes.</summary>
    private static (string? Path, byte[]? Bytes) ReadPredictedAdml(string sourceFolder, string stem, string? uiCulture)
    {
        var culture = uiCulture?.Trim();
        if (string.IsNullOrEmpty(culture) || culture is "." or ".." || culture.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            culture = FallbackCulture;
        }

        foreach (var language in new[] { culture, FallbackCulture })
        {
            try
            {
                var folder = Path.Combine(sourceFolder, language);
                var path = Path.Combine(folder, stem + TemplateFileNames.AdmlExtension);
                if (Directory.Exists(folder) && !TemplateStoreChecks.IsReparsePoint(folder) && File.Exists(path))
                {
                    return (path, TryRead(path));
                }
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {
                continue;
            }
        }

        return (null, null);
    }

    private TemplateImportProblem? Verify(
        string templateNamespace, string admxPath, string admlPath, string? uiCulture, CancellationToken cancellationToken)
    {
        var loaded = sources.Load(uiCulture, cancellationToken);
        var file = loaded.Set.FindFile(templateNamespace);
        var failure = loaded.Set.Failures.FirstOrDefault(f =>
            TemplateStoreChecks.IsSame(f.FilePath, admxPath) || TemplateStoreChecks.IsSame(f.FilePath, admlPath));
        if (file is not null && failure is null && TemplateStoreChecks.IsSame(file.AdmxPath, admxPath))
        {
            return null;
        }

        return new TemplateImportProblem(
            TemplateImportProblemCode.VerifyFailed, admxPath, templateNamespace, failure?.Code);
    }

    private List<TemplateImportProblem> Dependents(string admxPath, CancellationToken cancellationToken)
    {
        var set = TemplateLoader.Load([sources.StoreFolder], null, cancellationToken);
        var self = set.Files.FirstOrDefault(f => TemplateStoreChecks.IsSame(f.AdmxPath, admxPath));
        var result = new List<TemplateImportProblem>();
        if (self is null)
        {
            return result;
        }

        foreach (var other in set.Files.Where(f => !ReferenceEquals(f, self)))
        {
            if (other.Usings.Any(u => string.Equals(u.Namespace, self.Namespace, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(new TemplateImportProblem(TemplateImportProblemCode.UsedByTemplate, other.AdmxPath, self.Namespace));
            }
        }

        return result;
    }

    private TemplateImportProblem? StoreProblem(TemplateStoreStatus status, string detail) =>
        status switch
        {
            TemplateStoreStatus.Ready => null,
            TemplateStoreStatus.Missing => new TemplateImportProblem(TemplateImportProblemCode.NotImported, sources.StoreFolder),
            _ => new TemplateImportProblem(TemplateImportProblemCode.StoreUnavailable, sources.StoreFolder, status + ": " + detail),
        };

    private static string? ReadName(string? admxPath, List<TemplateImportProblem> problems)
    {
        var full = TemplateStoreChecks.Normalize(admxPath);
        if (full is null)
        {
            problems.Add(new TemplateImportProblem(TemplateImportProblemCode.SourceMissing, admxPath ?? string.Empty));
            return null;
        }

        var extension = Path.GetExtension(full);
        if (extension.Equals(".adm", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(new TemplateImportProblem(TemplateImportProblemCode.ClassicAdmNotSupported, full));
            return null;
        }

        if (!extension.Equals(TemplateFileNames.AdmxExtension, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(new TemplateImportProblem(TemplateImportProblemCode.NotAdmx, full));
            return null;
        }

        var admxName = Path.GetFileNameWithoutExtension(full) + TemplateFileNames.AdmxExtension;
        if (!TemplateFileNames.IsSafeAdmxFileName(admxName))
        {
            problems.Add(new TemplateImportProblem(TemplateImportProblemCode.UnsafeFileName, full, admxName));
            return null;
        }

        return full;
    }

    private static byte[]? ReadBounded(string path, List<TemplateImportProblem> problems)
    {
        try
        {
            if (!File.Exists(path))
            {
                problems.Add(new TemplateImportProblem(TemplateImportProblemCode.SourceMissing, path));
                return null;
            }

            if (TemplateStoreChecks.IsReparsePoint(path))
            {
                problems.Add(new TemplateImportProblem(TemplateImportProblemCode.SourceReparsePoint, path));
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > AdmxLimits.MaxFileBytes)
            {
                problems.Add(new TemplateImportProblem(TemplateImportProblemCode.SourceTooLarge, path));
                return null;
            }

            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add(new TemplateImportProblem(
                TemplateImportProblemCode.SourceUnreadable, path, "0x" + e.HResult.ToString("X8", CultureInfo.InvariantCulture)));
            return null;
        }
    }

    private static bool IsRelevant(TemplateFailure failure, string admxPath, string sourceFolder, string stem, string? uiCulture)
    {
        if (TemplateStoreChecks.IsSame(failure.FilePath, admxPath)
            || TemplateStoreChecks.IsSame(failure.Detail, admxPath)
            || TemplateStoreChecks.IsSame(failure.FilePath, sourceFolder))
        {
            return true;
        }

        var cultureName = string.IsNullOrWhiteSpace(uiCulture) ? FallbackCulture : uiCulture.Trim();
        foreach (var language in new[] { cultureName, FallbackCulture })
        {
            string folder;
            try
            {
                folder = Path.Combine(sourceFolder, language);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (TemplateStoreChecks.IsSame(failure.FilePath, folder))
            {
                return true;
            }

            if (TemplateStoreChecks.IsUnder(folder, failure.FilePath)
                && string.Equals(Path.GetExtension(failure.FilePath), TemplateFileNames.AdmlExtension, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileNameWithoutExtension(failure.FilePath), stem, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static TemplateImportProblem MapFailure(TemplateFailure failure, TemplateSet set)
    {
        var code = failure.Code switch
        {
            TemplateFailureCode.NamespaceShadowed => set.FindFile(failure.Detail)?.SourceIndex == TemplateSources.SystemSourceIndex
                ? TemplateImportProblemCode.NamespaceDefinedBySystem
                : TemplateImportProblemCode.NamespaceAlreadyImported,
            TemplateFailureCode.MissingAdml => TemplateImportProblemCode.AdmlMissing,
            TemplateFailureCode.ReparsePoint => TemplateImportProblemCode.SourceReparsePoint,
            TemplateFailureCode.FileTooLarge => TemplateImportProblemCode.SourceTooLarge,
            TemplateFailureCode.Unreadable => TemplateImportProblemCode.SourceUnreadable,
            TemplateFailureCode.FolderNotFound => TemplateImportProblemCode.SourceMissing,
            _ => TemplateImportProblemCode.LoadFailed,
        };
        return new TemplateImportProblem(code, failure.FilePath, failure.Detail, failure.Code);
    }

    private static TemplateImportProblem FromStoreException(TemplateStoreException e)
    {
        var code = e.Reason is TemplateStoreFailure.StoreRootForbidden or TemplateStoreFailure.StoreRootNotLocal
            or TemplateStoreFailure.StoreAclRejected
            ? TemplateImportProblemCode.StoreUnavailable
            : TemplateImportProblemCode.WriteFailed;
        return new TemplateImportProblem(code, e.Path, e.Operation, null, e.Reason, e.Win32Error);
    }
}
