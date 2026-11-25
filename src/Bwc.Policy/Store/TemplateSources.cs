using Bwc.Policy.Admx;
using Bwc.Update;

namespace Bwc.Policy.Store;

/// <summary>An imported template that a system template of the same namespace hides.</summary>
/// <param name="Path">The ADMX file of the import store.</param>
/// <param name="Namespace">The namespace both files define.</param>
/// <param name="WinnerPath">The ADMX file that won.</param>
public sealed record ShadowedTemplate(string Path, string Namespace, string WinnerPath);

/// <summary>The templates of both sources, the state of the import store and the imports that lost.</summary>
public sealed class TemplateSourcesResult
{
    internal TemplateSourcesResult(
        TemplateSet set,
        TemplateStoreStatus store,
        string storeDetail,
        IReadOnlyList<ShadowedTemplate> shadowed,
        IReadOnlyList<TemplateFile> imported)
    {
        Set = set;
        Store = store;
        StoreDetail = storeDetail;
        Shadowed = shadowed;
        ImportedFiles = imported;
    }

    /// <summary>The linked set: the system folder first, then the import store when it was loaded.</summary>
    public TemplateSet Set { get; }

    /// <summary>The state of the import store. Only <see cref="TemplateStoreStatus.Ready"/> is loaded.</summary>
    public TemplateStoreStatus Store { get; }

    /// <summary>The reason for a rejected ACL or the HRESULT of an unreadable store, else empty.</summary>
    public string StoreDetail { get; }

    /// <summary>The imports that a system template hides, in file order.</summary>
    public IReadOnlyList<ShadowedTemplate> Shadowed { get; }

    /// <summary>The loaded files that came from the import store.</summary>
    public IReadOnlyList<TemplateFile> ImportedFiles { get; }
}

/// <summary>
/// The two template sources: the system folder and the BWC import store. The library reads no environment variable; the
/// tool passes both folders (the E2E sandbox passes its own roots). The system folder wins on a duplicate namespace.
/// The store is loaded only when its owner and access list pass the ACL rules, so a folder that a user could write is
/// never read by an elevated process (docs/failure-modes/template-store.md, A3).
/// </summary>
public sealed class TemplateSources
{
    /// <summary>The index of the system folder in the loaded set (<see cref="TemplateFile.SourceIndex"/>).</summary>
    public const int SystemSourceIndex = 0;

    /// <summary>The index of the import store in the loaded set, when it was loaded.</summary>
    public const int StoreSourceIndex = 1;

    /// <summary>Creates the sources.</summary>
    /// <param name="systemFolder">The system template folder.</param>
    /// <param name="storeFolder">The import store folder.</param>
    /// <param name="acl">The ACL rules the store must satisfy.</param>
    public TemplateSources(string systemFolder, string storeFolder, ProtectedAcl acl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeFolder);
        ArgumentNullException.ThrowIfNull(acl);
        SystemFolder = systemFolder;
        StoreFolder = storeFolder;
        Acl = acl;
    }

    /// <summary>The system template folder.</summary>
    public string SystemFolder { get; }

    /// <summary>The import store folder.</summary>
    public string StoreFolder { get; }

    /// <summary>The ACL rules of the store.</summary>
    public ProtectedAcl Acl { get; }

    /// <summary>A read-only look at the store folder.</summary>
    public (TemplateStoreStatus Status, string Detail) InspectStore()
    {
        var full = TemplateStoreChecks.Normalize(StoreFolder);
        return full is null
            ? (TemplateStoreStatus.Unreadable, StoreFolder)
            : TemplateStoreChecks.Inspect(full, Acl);
    }

    /// <summary>Loads the system folder and, when it is usable, the import store.</summary>
    /// <param name="uiCulture">The UI culture name, or null for en-US.</param>
    /// <param name="cancellationToken">Checked once per file.</param>
    public TemplateSourcesResult Load(string? uiCulture = null, CancellationToken cancellationToken = default)
    {
        var (status, detail) = InspectStore();
        var folders = status == TemplateStoreStatus.Ready ? new[] { SystemFolder, StoreFolder } : [SystemFolder];
        var set = TemplateLoader.Load(folders, uiCulture, cancellationToken);
        var shadowed = new List<ShadowedTemplate>();
        foreach (var failure in set.Failures)
        {
            if (failure.Code == TemplateFailureCode.NamespaceShadowed && IsInStore(failure.FilePath))
            {
                shadowed.Add(new ShadowedTemplate(failure.FilePath, failure.Detail, set.FindFile(failure.Detail)?.AdmxPath ?? string.Empty));
            }
        }

        var imported = status == TemplateStoreStatus.Ready
            ? set.Files.Where(f => f.SourceIndex == StoreSourceIndex).ToArray()
            : [];
        return new TemplateSourcesResult(set, status, detail, shadowed, imported);
    }

    private bool IsInStore(string path)
    {
        var store = TemplateStoreChecks.Normalize(StoreFolder);
        var full = TemplateStoreChecks.Normalize(path);
        return store is not null && full is not null && TemplateStoreChecks.IsUnder(store, full);
    }
}
