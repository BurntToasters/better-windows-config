using Bwc.Update;

namespace Bwc.Policy.Store;

/// <summary>
/// The only code that writes into the BWC import store (scripts/check-policy-writer.js). It never names the system
/// folder as a target: the system folder is passed only so a store root that is, contains or lies inside it is refused.
/// Folders are created with the protected ACL, every file is written to a temp file in its own folder and moved into
/// place, and a name is one safe segment that is re-checked here whatever the caller did. The cases are in
/// docs/failure-modes/template-store.md, sections A, D, E and F.
/// </summary>
public sealed class TemplateStoreWriter
{
    private readonly ProtectedAcl acl;
    private readonly string systemFolder;

    /// <summary>Creates a writer. Nothing is touched until a method is called.</summary>
    /// <param name="storeRoot">The import store folder (an absolute local path).</param>
    /// <param name="systemFolder">The system template folder. Only used to refuse a store root that overlaps it.</param>
    /// <param name="acl">The folder rules: ship rules in the ship build, current user rules in tests and E2E.</param>
    /// <exception cref="TemplateStoreException">The root is not usable (<see cref="TemplateStoreFailure.StoreRootForbidden"/>
    /// or <see cref="TemplateStoreFailure.StoreRootNotLocal"/>).</exception>
    public TemplateStoreWriter(string storeRoot, string systemFolder, ProtectedAcl acl)
    {
        ArgumentNullException.ThrowIfNull(acl);
        this.acl = acl;
        var root = TemplateStoreChecks.Normalize(storeRoot)
            ?? throw TemplateStoreException.Refused(TemplateStoreFailure.StoreRootForbidden, "Open", storeRoot ?? string.Empty, "the store folder is not an absolute path");
        var system = TemplateStoreChecks.Normalize(systemFolder)
            ?? throw TemplateStoreException.Refused(TemplateStoreFailure.StoreRootForbidden, "Open", systemFolder ?? string.Empty, "the system folder is not an absolute path");
        if (TemplateStoreChecks.IsNotLocal(root))
        {
            throw TemplateStoreException.Refused(TemplateStoreFailure.StoreRootNotLocal, "Open", root, "the store folder is on a network path");
        }

        if (Path.GetPathRoot(root)?.Length == root.Length)
        {
            throw TemplateStoreException.Refused(TemplateStoreFailure.StoreRootForbidden, "Open", root, "the store folder is a drive root");
        }

        if (TemplateStoreChecks.IsSame(root, system) || TemplateStoreChecks.IsUnder(system, root) || TemplateStoreChecks.IsUnder(root, system))
        {
            throw TemplateStoreException.Refused(TemplateStoreFailure.StoreRootForbidden, "Open", root, "the store folder overlaps the system folder");
        }

        StoreRoot = root;
        this.systemFolder = system;
    }

    /// <summary>The full path of the store folder.</summary>
    public string StoreRoot { get; }

    /// <summary>Creates the store folder with the protected ACL when it is missing, then checks it.</summary>
    /// <exception cref="TemplateStoreException">The store cannot be created or is not acceptable.</exception>
    public void EnsureStore() => EnsureFolder(StoreRoot);

    /// <summary>
    /// Writes a template: the ADML into its language folder first, then the ADMX. When the ADMX write fails, an ADML
    /// that this call created is removed again.
    /// </summary>
    /// <param name="admxFileName">The ADMX file name (a safe segment ending in <c>.admx</c>).</param>
    /// <param name="admx">The ADMX bytes.</param>
    /// <param name="culture">The language folder name for the ADML (a safe segment).</param>
    /// <param name="adml">The ADML bytes.</param>
    /// <exception cref="TemplateStoreException">Nothing is left half written, except an orphan ADML no load reads.</exception>
    public void WriteTemplate(string admxFileName, byte[] admx, string culture, byte[] adml)
    {
        ArgumentNullException.ThrowIfNull(admx);
        ArgumentNullException.ThrowIfNull(adml);
        RequireAdmxName(admxFileName);
        RequireSegment(culture, "culture");
        EnsureStore();
        var languageFolder = Path.Combine(StoreRoot, culture);
        EnsureFolder(languageFolder);
        var admlPath = Path.Combine(languageFolder, TemplateFileNames.AdmlNameOf(admxFileName));
        var admlExisted = File.Exists(admlPath);
        WriteFile(languageFolder, TemplateFileNames.AdmlNameOf(admxFileName), adml);
        try
        {
            WriteFile(StoreRoot, admxFileName, admx);
        }
        catch (TemplateStoreException) when (!admlExisted)
        {
            TryDelete(admlPath);
            throw;
        }
    }

    /// <summary>
    /// Removes a template: the ADMX first, so the template leaves the set, then its ADML from every language folder
    /// directly under the store. A language folder that is a reparse point is skipped.
    /// </summary>
    /// <param name="admxFileName">The ADMX file name.</param>
    /// <returns>The paths that were deleted.</returns>
    /// <exception cref="TemplateStoreException">A delete failed. The ADMX is gone if only an ADML delete failed.</exception>
    public IReadOnlyList<string> RemoveTemplate(string admxFileName)
    {
        RequireAdmxName(admxFileName);
        var removed = new List<string>();
        if (!Directory.Exists(StoreRoot))
        {
            return removed;
        }

        CheckFolder(StoreRoot);
        DeleteIfPresent(Path.Combine(StoreRoot, admxFileName), removed);
        TemplateStoreException? failure = null;
        foreach (var folder in EnumerateLanguageFolders())
        {
            try
            {
                DeleteIfPresent(Path.Combine(folder, TemplateFileNames.AdmlNameOf(admxFileName)), removed);
            }
            catch (TemplateStoreException e)
            {
                failure ??= e;
            }
        }

        return failure is null ? removed : throw failure;
    }

    private List<string> EnumerateLanguageFolders()
    {
        var folders = new List<string>();
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(StoreRoot))
            {
                if (!TemplateStoreChecks.IsReparsePoint(folder))
                {
                    folders.Add(folder);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw TemplateStoreException.FromIo("Enumerate", StoreRoot, e);
        }

        return folders;
    }

    private void DeleteIfPresent(string path, List<string> removed)
    {
        RequireDirectChild(path);
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            if (TemplateStoreChecks.IsReparsePoint(path))
            {
                throw TemplateStoreException.Refused(TemplateStoreFailure.ReparsePoint, "Delete", path, "the file is a reparse point");
            }

            File.Delete(path);
            removed.Add(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw TemplateStoreException.FromIo("Delete", path, e);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
    }

    private void EnsureFolder(string folder)
    {
        try
        {
            if (File.Exists(folder))
            {
                throw TemplateStoreException.Refused(TemplateStoreFailure.NotADirectory, "Create", folder, "the path is a file");
            }

            if (!Directory.Exists(folder))
            {
                acl.NewSecurity().CreateDirectory(folder);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw TemplateStoreException.FromIo("Create", folder, e);
        }

        CheckFolder(folder);
    }

    private void CheckFolder(string folder)
    {
        var (status, detail) = TemplateStoreChecks.Inspect(folder, acl);
        switch (status)
        {
            case TemplateStoreStatus.Ready:
                return;
            case TemplateStoreStatus.NotADirectory:
                throw TemplateStoreException.Refused(TemplateStoreFailure.NotADirectory, "Check", folder, "the path is a file");
            case TemplateStoreStatus.ReparsePoint:
                throw TemplateStoreException.Refused(TemplateStoreFailure.ReparsePoint, "Check", folder, "the folder is a reparse point");
            case TemplateStoreStatus.AclRejected:
                throw TemplateStoreException.Refused(TemplateStoreFailure.StoreAclRejected, "Check", folder, detail);
            default:
                throw TemplateStoreException.Refused(TemplateStoreFailure.Io, "Check", folder, "the folder cannot be inspected (" + detail + ")");
        }
    }

    private void WriteFile(string folder, string name, byte[] data)
    {
        var target = Path.Combine(folder, name);
        RequireDirectChild(target);
        CheckFolder(folder);
        if (Directory.Exists(target))
        {
            throw TemplateStoreException.Refused(TemplateStoreFailure.Io, "Write", target, "the target is a folder");
        }

        var exists = File.Exists(target);
        if (exists && TemplateStoreChecks.IsReparsePoint(target))
        {
            throw TemplateStoreException.Refused(TemplateStoreFailure.ReparsePoint, "Write", target, "the target is a reparse point");
        }

        var temp = Path.Combine(folder, name + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var operation = "Write";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }

            operation = exists ? "Replace" : "Move";
            if (exists)
            {
                File.Replace(temp, target, null, ignoreMetadataErrors: false);
            }
            else
            {
                File.Move(temp, target);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw TemplateStoreException.FromIo(operation, target, e);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private void RequireAdmxName(string name)
    {
        if (!TemplateFileNames.IsSafeAdmxFileName(name))
        {
            throw TemplateStoreException.Refused(TemplateStoreFailure.UnsafeName, "Name", name ?? string.Empty, "the file name is not one safe .admx name");
        }
    }

    private static void RequireSegment(string name, string what)
    {
        if (!TemplateFileNames.IsSafeSegment(name))
        {
            throw TemplateStoreException.Refused(TemplateStoreFailure.UnsafeName, "Name", name ?? string.Empty, "the " + what + " name is not one safe segment");
        }
    }

    private void RequireDirectChild(string path)
    {
        var full = TemplateStoreChecks.Normalize(path);
        var parent = full is null ? null : Path.GetDirectoryName(full);
        var grand = parent is null ? null : Path.GetDirectoryName(parent);
        var ok = parent is not null
            && (TemplateStoreChecks.IsSame(parent, StoreRoot) || (grand is not null && TemplateStoreChecks.IsSame(grand, StoreRoot)));
        if (!ok || TemplateStoreChecks.IsSame(StoreRoot, systemFolder) || TemplateStoreChecks.IsUnder(systemFolder, full!))
        {
            throw TemplateStoreException.Refused(TemplateStoreFailure.UnsafeName, "Name", path, "the path is not directly in the store or one of its language folders");
        }
    }
}
