using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace Bwc.Registry;

/// <summary>
/// An absolute registry path made of a predefined root and zero or more key components.
/// Equality and descendant checks are case-insensitive, matching the registry.
/// </summary>
public sealed class RegistryPath : IEquatable<RegistryPath>
{
    internal const int MaxComponentLength = 255;

    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
    private static readonly ReadOnlyCollection<string> EmptyComponents = Array.AsReadOnly(Array.Empty<string>());

    private readonly string[] _components;
    private readonly int _hashCode;

    private RegistryPath(RegistryRoot root, string[] components)
    {
        Root = root;
        _components = components;
        Components = components.Length == 0 ? EmptyComponents : Array.AsReadOnly(components);
        Parent = components.Length == 0
            ? null
            : new RegistryPath(root, components[..^1]);

        var hash = new HashCode();
        hash.Add((int)root);
        foreach (string component in components)
        {
            hash.Add(component, Comparer);
        }

        _hashCode = hash.ToHashCode();
    }

    public RegistryRoot Root { get; }

    public IReadOnlyList<string> Components { get; }

    public RegistryPath? Parent { get; }

    public bool IsRoot => _components.Length == 0;

    public static RegistryPath Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!TryParseCore(path, out RegistryPath? result))
        {
            throw new FormatException($"The registry path '{path}' is not a valid absolute registry path.");
        }

        return result;
    }

    public static bool TryParse(string? path, [NotNullWhen(true)] out RegistryPath? result)
    {
        if (path is null)
        {
            result = null;
            return false;
        }

        return TryParseCore(path, out result);
    }

    public RegistryPath Append(string component)
    {
        ArgumentNullException.ThrowIfNull(component);

        if (!IsValidComponent(component))
        {
            throw new ArgumentException(
                "A registry key component must be non-empty, at most 255 characters, and must not contain a separator, NUL, '.' or '..'.",
                nameof(component));
        }

        return Extend(component);
    }

    /// <summary>
    /// Appends a key name the registry enumerated. The registry allows "." and ".." as key names, so
    /// unlike <see cref="Append"/> this accepts them. Separators, NUL, and empty or overlong names are
    /// still refused. User-typed paths must keep using <see cref="Append"/>.
    /// </summary>
    public RegistryPath AppendEnumeratedName(string component)
    {
        ArgumentNullException.ThrowIfNull(component);

        if (component is not ("." or "..") && !IsValidComponent(component))
        {
            throw new ArgumentException(
                "A registry key name must be non-empty, at most 255 characters, and must not contain a separator or NUL.",
                nameof(component));
        }

        return Extend(component);
    }

    private RegistryPath Extend(string component)
    {
        string[] next = new string[_components.Length + 1];
        Array.Copy(_components, next, _components.Length);
        next[^1] = component;
        return new RegistryPath(Root, next);
    }

    public bool IsDescendantOf(RegistryPath other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (Root != other.Root || _components.Length <= other._components.Length)
        {
            return false;
        }

        for (int index = 0; index < other._components.Length; index += 1)
        {
            if (!Comparer.Equals(_components[index], other._components[index]))
            {
                return false;
            }
        }

        return true;
    }

    public string ToDisplayString()
    {
        string root = Root.DisplayName();
        return _components.Length == 0
            ? $"Computer\\{root}"
            : $"Computer\\{root}\\{string.Join('\\', _components)}";
    }

    public string ToAbbreviatedString()
    {
        string root = AbbreviatedName(Root);
        return _components.Length == 0
            ? root
            : $"{root}\\{string.Join('\\', _components)}";
    }

    public override string ToString() => ToDisplayString();

    public bool Equals(RegistryPath? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (Root != other.Root || _components.Length != other._components.Length)
        {
            return false;
        }

        for (int index = 0; index < _components.Length; index += 1)
        {
            if (!Comparer.Equals(_components[index], other._components[index]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as RegistryPath);

    public override int GetHashCode() => _hashCode;

    internal static bool IsValidComponent(string? component)
    {
        if (string.IsNullOrEmpty(component) || component.Length > MaxComponentLength)
        {
            return false;
        }

        if (component is "." or "..")
        {
            return false;
        }

        foreach (char character in component)
        {
            if (character is '\\' or '\0')
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseCore(string path, [NotNullWhen(true)] out RegistryPath? result)
    {
        result = null;

        if (path.Length == 0)
        {
            return false;
        }

        string[] segments = path.Split('\\');
        int index = 0;

        if (segments[0].Equals("Computer", StringComparison.OrdinalIgnoreCase))
        {
            index = 1;
            if (index >= segments.Length)
            {
                return false;
            }
        }

        if (!TryParseRoot(segments[index], out RegistryRoot root))
        {
            return false;
        }

        index += 1;
        int componentCount = segments.Length - index;
        string[] components = new string[componentCount];

        for (int componentIndex = 0; componentIndex < componentCount; componentIndex += 1)
        {
            string component = segments[index + componentIndex];
            if (!IsValidComponent(component))
            {
                return false;
            }

            components[componentIndex] = component;
        }

        result = new RegistryPath(root, components);
        return true;
    }

    private static bool TryParseRoot(string token, out RegistryRoot root)
    {
        switch (token.ToUpperInvariant())
        {
            case "HKEY_CLASSES_ROOT":
            case "HKCR":
                root = RegistryRoot.ClassesRoot;
                return true;
            case "HKEY_CURRENT_USER":
            case "HKCU":
                root = RegistryRoot.CurrentUser;
                return true;
            case "HKEY_LOCAL_MACHINE":
            case "HKLM":
                root = RegistryRoot.LocalMachine;
                return true;
            case "HKEY_USERS":
            case "HKU":
                root = RegistryRoot.Users;
                return true;
            case "HKEY_CURRENT_CONFIG":
            case "HKCC":
                root = RegistryRoot.CurrentConfig;
                return true;
            default:
                root = default;
                return false;
        }
    }

    private static string AbbreviatedName(RegistryRoot root) => root switch
    {
        RegistryRoot.ClassesRoot => "HKCR",
        RegistryRoot.CurrentUser => "HKCU",
        RegistryRoot.LocalMachine => "HKLM",
        RegistryRoot.Users => "HKU",
        RegistryRoot.CurrentConfig => "HKCC",
        _ => throw new ArgumentOutOfRangeException(nameof(root)),
    };
}
