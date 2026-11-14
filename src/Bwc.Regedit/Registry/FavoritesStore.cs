using Bwc.Core;
using Bwc.Registry;

namespace Bwc.Regedit.Registry;

/// <summary>One classic Registry Editor favorite: a display name and the key it points at.</summary>
public sealed class Favorite
{
    public Favorite(string name, RegistryPath path)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(path);

        Name = name;
        Path = path;
    }

    /// <summary>The value name under the Favorites key. Unique, case-insensitive.</summary>
    public string Name { get; }

    /// <summary>The stored key path as parsed from the value data.</summary>
    public RegistryPath Path { get; }

    /// <summary>The canonical <c>Computer\HKEY_...</c> path written to the registry.</summary>
    public string DisplayPath => Path.ToDisplayString();
}

/// <summary>
/// Reads and writes the classic Favorites value list (BEH-001). Each favorite is one REG_SZ value
/// whose name is the display name and whose data is the full <c>Computer\HKEY_...</c> path. The
/// full display form is used so a stored path can go straight into <see cref="RegistryPath"/> and
/// the tree source without a second abbreviation step.
/// </summary>
public sealed class FavoritesStore
{
    /// <summary>The key classic regedit uses. Shared on purpose (BEH-001).</summary>
    public const string ClassicKeyPath =
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Applets\Regedit\Favorites";

    /// <summary>The exact RT_STRING 171 text for a duplicate favorite name.</summary>
    public const string DuplicateNameMessage = "There is already a favorite with that name.";

#if BWC_E2E
    /// <summary>E2E-only override so a test never touches the developer's real favorites.</summary>
    public const string RootEnvironmentVariable = "BWC_E2E_FAVORITES_ROOT";

    private const string SandboxRootEnvironmentVariable = "BWC_E2E_SANDBOX_ROOT";

    private static readonly RegistryPath SandboxBase =
        RegistryPath.Parse(@"HKCU\Software\BWC-E2E");
#endif

    private const int MaxNameLength = 16383;

    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    private readonly RegistryStore _store;
    private readonly RegistryReader _reader;
    private IReadOnlyList<Favorite> _favorites = Array.Empty<Favorite>();

    public FavoritesStore()
    {
        _store = RegeditWritePolicy.CreateStore();
        _reader = _store.Reader;
        FavoritesKeyPath = ResolveFavoritesKeyPath();
    }

    /// <summary>The key this store reads and writes. Classic unless an E2E override applies.</summary>
    public RegistryPath FavoritesKeyPath { get; }

    /// <summary>
    /// The last successful load, sorted case-insensitively by display name. A failed load keeps the
    /// previous list so the menu does not collapse to empty after a transient read error.
    /// </summary>
    public IReadOnlyList<Favorite> Favorites => _favorites;

    /// <summary>The message from the most recent failed load, or null after a successful one.</summary>
    public string? LoadError { get; private set; }

    /// <summary>The exception from the most recent failed load, or null after a successful one.</summary>
    public Exception? LoadException { get; private set; }

    /// <summary>Case-insensitive name lookup against the last successfully loaded list.</summary>
    public bool ContainsName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        foreach (Favorite favorite in _favorites)
        {
            if (NameComparer.Equals(favorite.Name, name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads every REG_SZ value under <see cref="FavoritesKeyPath"/>. A missing key is an empty
    /// list. Invalid paths and values of other types are skipped with a warning. This never throws:
    /// the caller inspects <see cref="LoadError"/>.
    /// </summary>
    public void Reload()
    {
        LoadError = null;
        LoadException = null;

        try
        {
            if (!_reader.KeyExists(FavoritesKeyPath))
            {
                _favorites = Array.Empty<Favorite>();
                return;
            }

            IReadOnlyList<RegistryValueInfo> values = _reader.EnumerateValues(FavoritesKeyPath);
            var loaded = new List<Favorite>(values.Count);

            foreach (RegistryValueInfo value in values)
            {
                // The default value is not a favorite. Loading it as one emptied the whole list.
                if (value.Name.Length == 0)
                {
                    continue;
                }

                if (value.Type != (uint)RegistryValueType.String)
                {
                    Log.Warn(
                        $"Registry Editor skipped favorite '{value.Name}': type {value.Type} is not REG_SZ.");
                    continue;
                }

                string text = RegistryValueCodec.DecodeString(value.Data).Trim();
                if (!RegistryPath.TryParse(text, out RegistryPath? path) || path is null)
                {
                    Log.Warn(
                        $"Registry Editor skipped favorite '{value.Name}': '{text}' is not a valid registry path.");
                    continue;
                }

                loaded.Add(new Favorite(value.Name, path));
            }

            loaded.Sort((left, right) => NameComparer.Compare(left.Name, right.Name));
            _favorites = loaded;
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
            LoadException = ex;
            Log.Warn("Registry Editor could not load favorites: " + ex.Message);
        }
    }

    /// <summary>
    /// Writes one REG_SZ favorite. A duplicate name is rejected case-insensitively with the
    /// RT_STRING 171 message instead of overwriting the existing value.
    /// </summary>
    public void Add(string name, RegistryPath path)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(path);

        // The name is kept as given. Classic regedit does not trim it, so a trailing space is part of it.
        string displayName = name;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Enter a favorite name.", nameof(name));
        }

        if (displayName.Length > MaxNameLength || displayName.Contains('\0'))
        {
            throw new ArgumentException(
                $"A favorite name must be at most {MaxNameLength} characters and cannot contain NUL.",
                nameof(name));
        }

        Reload();
        if (LoadError is not null)
        {
            throw new InvalidOperationException("The favorites list could not be read: " + LoadError, LoadException);
        }

        if (ContainsName(displayName))
        {
            throw new ArgumentException(DuplicateNameMessage, nameof(name));
        }

        EnsureKeyExists();
        _store.SetString(FavoritesKeyPath, displayName, path.ToDisplayString());
        Reload();
    }

    /// <summary>
    /// Removes each named favorite. A value that disappeared between the read and the delete is
    /// treated as already removed; other write failures propagate.
    /// </summary>
    public void Remove(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        Reload();
        if (LoadError is not null)
        {
            throw new InvalidOperationException("The favorites list could not be read: " + LoadError, LoadException);
        }

        if (!_reader.KeyExists(FavoritesKeyPath))
        {
            _favorites = Array.Empty<Favorite>();
            return;
        }

        foreach (string name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            try
            {
                _store.DeleteValue(FavoritesKeyPath, name);
            }
            catch (RegistryOperationException ex) when (ex.Win32Error == 2)
            {
                // The value vanished after the reload. It is already in the desired state.
                Log.Info($"Registry Editor favorite '{name}' was already removed.");
            }
        }

        Reload();
    }

    private void EnsureKeyExists()
    {
        var missing = new Stack<RegistryPath>();
        RegistryPath? current = FavoritesKeyPath;

        while (current is not null && !_reader.KeyExists(current))
        {
            missing.Push(current);
            current = current.Parent;
        }

        while (missing.Count > 0)
        {
            _store.CreateKey(missing.Pop());
        }
    }

    private static RegistryPath ResolveFavoritesKeyPath()
    {
        RegistryPath classic = RegistryPath.Parse(ClassicKeyPath);

#if BWC_E2E
        string? configured = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) &&
            RegistryPath.TryParse(configured.Trim(), out RegistryPath? candidate) &&
            candidate is not null &&
            TryGetSandboxRoot(out RegistryPath? sandboxRoot) &&
            sandboxRoot is not null &&
            (candidate.Equals(sandboxRoot) || candidate.IsDescendantOf(sandboxRoot)))
        {
            Log.Info("Registry Editor favorites key: " + candidate.ToDisplayString());
            return candidate;
        }

        Log.Info("Registry Editor favorites key: " + classic.ToDisplayString() + " (classic, E2E override not usable)");
#else
        Log.Info("Registry Editor favorites key: " + classic.ToDisplayString());
#endif

        return classic;
    }

#if BWC_E2E
    private static bool TryGetSandboxRoot(out RegistryPath? root)
    {
        root = null;
        string? configured = Environment.GetEnvironmentVariable(SandboxRootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured) ||
            !RegistryPath.TryParse(configured.Trim(), out root) ||
            root is null)
        {
            return false;
        }

        return root.Root == RegistryRoot.CurrentUser &&
            (root.Equals(SandboxBase) || root.IsDescendantOf(SandboxBase));
    }
#endif
}
