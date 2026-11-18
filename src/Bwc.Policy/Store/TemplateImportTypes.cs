using Bwc.Policy.Admx;

namespace Bwc.Policy.Store;

/// <summary>A reason an import or a removal is refused. The UI picks the wording.</summary>
public enum TemplateImportProblemCode
{
    /// <summary>The path is empty, not valid, or names no file.</summary>
    SourceMissing,

    /// <summary>The file is a classic <c>.adm</c>, which BWC does not support.</summary>
    ClassicAdmNotSupported,

    /// <summary>The file is not an <c>.admx</c>.</summary>
    NotAdmx,

    /// <summary>The file name is not one safe name (device name, trailing dot, stream, too long).</summary>
    UnsafeFileName,

    /// <summary>The file, its folder or its language folder is a reparse point.</summary>
    SourceReparsePoint,

    /// <summary>The file is larger than <see cref="AdmxLimits.MaxFileBytes"/>.</summary>
    SourceTooLarge,

    /// <summary>The file cannot be read. The detail holds the HRESULT.</summary>
    SourceUnreadable,

    /// <summary>The file changed between the check and the copy.</summary>
    SourceChanged,

    /// <summary>The loader refused the file or its ADML. <see cref="TemplateImportProblem.LoadCode"/> says why.</summary>
    LoadFailed,

    /// <summary>No ADML exists for the UI culture or for en-US.</summary>
    AdmlMissing,

    /// <summary>The system folder already defines the namespace.</summary>
    NamespaceDefinedBySystem,

    /// <summary>The import store already defines the namespace.</summary>
    NamespaceAlreadyImported,

    /// <summary>A different template of the store has the same file name.</summary>
    FileNameInUse,

    /// <summary>The template uses a namespace that neither the system folder nor the store defines.</summary>
    UsingNotInstalled,

    /// <summary>The store is not usable (not a folder, a link, a rejected ACL, unreadable, or its root is refused).</summary>
    StoreUnavailable,

    /// <summary>The copy failed. <see cref="TemplateImportProblem.StoreFailure"/> and the Win32 error say why.</summary>
    WriteFailed,

    /// <summary>The copied template did not load from the store, and the copy was removed again.</summary>
    VerifyFailed,

    /// <summary>The namespace or file is not in the import store.</summary>
    NotImported,

    /// <summary>Another imported template uses the namespace. The detail is the dependent file.</summary>
    UsedByTemplate,
}

/// <summary>One problem of an import or a removal.</summary>
/// <param name="Code">The reason.</param>
/// <param name="Path">The file or folder it belongs to.</param>
/// <param name="Detail">The faulty identifier (a namespace, a file name, an HRESULT), or an empty string.</param>
/// <param name="LoadCode">The loader's code for <see cref="TemplateImportProblemCode.LoadFailed"/>, else null.</param>
/// <param name="StoreFailure">The reason of a <see cref="TemplateImportProblemCode.WriteFailed"/>, else null.</param>
/// <param name="Win32Error">The Win32 error of a write failure, else 0.</param>
public sealed record TemplateImportProblem(
    TemplateImportProblemCode Code,
    string Path,
    string Detail = "",
    TemplateFailureCode? LoadCode = null,
    TemplateStoreFailure? StoreFailure = null,
    int Win32Error = 0);

/// <summary>The outcome of checking a template before it is imported. Nothing was written.</summary>
public sealed class TemplateImportCheck
{
    internal TemplateImportCheck(IReadOnlyList<TemplateImportProblem> problems)
    {
        Problems = problems;
    }

    /// <summary>What stops the import. Empty when the template can be imported.</summary>
    public IReadOnlyList<TemplateImportProblem> Problems { get; }

    /// <summary>True when the template can be imported.</summary>
    public bool CanImport => Problems.Count == 0;

    /// <summary>The namespace of the template, when it loaded.</summary>
    public string? Namespace { get; internal init; }

    /// <summary>The file name it will have in the store, when it passed the name checks.</summary>
    public string? AdmxFileName { get; internal init; }

    /// <summary>The language folder of the ADML that will be copied.</summary>
    public string? Culture { get; internal init; }

    internal byte[]? Admx { get; init; }

    internal byte[]? Adml { get; init; }
}

/// <summary>The outcome of an import.</summary>
/// <param name="Imported">True when the pair was copied and loads from the store.</param>
/// <param name="Problems">Why it was not, or empty.</param>
/// <param name="Namespace">The namespace of the template, when it loaded.</param>
/// <param name="AdmxPath">The ADMX in the store, when imported.</param>
/// <param name="AdmlPath">The ADML in the store, when imported.</param>
public sealed record TemplateImportResult(
    bool Imported,
    IReadOnlyList<TemplateImportProblem> Problems,
    string? Namespace = null,
    string? AdmxPath = null,
    string? AdmlPath = null);

/// <summary>The outcome of removing an imported template.</summary>
/// <param name="Removed">True when the ADMX was deleted.</param>
/// <param name="Problems">Why it was not, or what failed after the ADMX was gone.</param>
/// <param name="RemovedPaths">The files that were deleted.</param>
public sealed record TemplateRemoveResult(bool Removed, IReadOnlyList<TemplateImportProblem> Problems, IReadOnlyList<string> RemovedPaths);
