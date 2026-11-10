namespace Bwc.Registry;

/// <summary>The position a search has already considered.</summary>
public enum RegistryFindCursorPhase
{
    AfterKeyName,
    AfterValueName,
    AfterValueData,
}

/// <summary>
/// A position to resume after. A fresh Find with no cursor starts at the first search root.
/// <see cref="FromKeySelection"/> and <see cref="FromValueSelection"/> map the tree selection to
/// the contract's "start after the current selection" rule.
/// </summary>
public sealed class RegistryFindCursor
{
    public RegistryFindCursor(
        RegistryPath keyPath,
        RegistryFindCursorPhase phase,
        string? valueName = null)
    {
        ArgumentNullException.ThrowIfNull(keyPath);

        switch (phase)
        {
            case RegistryFindCursorPhase.AfterKeyName:
                if (valueName is not null)
                {
                    throw new ArgumentException(
                        "A key-name cursor must not name a value.",
                        nameof(valueName));
                }

                break;
            case RegistryFindCursorPhase.AfterValueName:
            case RegistryFindCursorPhase.AfterValueData:
                if (valueName is null)
                {
                    throw new ArgumentException(
                        "A value cursor must name a value. The default value name is the empty string.",
                        nameof(valueName));
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(phase));
        }

        KeyPath = keyPath;
        Phase = phase;
        ValueName = valueName;
    }

    public RegistryPath KeyPath { get; }

    public RegistryFindCursorPhase Phase { get; }

    public string? ValueName { get; }

    public static RegistryFindCursor FromKeySelection(RegistryPath keyPath)
    {
        ArgumentNullException.ThrowIfNull(keyPath);
        return new RegistryFindCursor(keyPath, RegistryFindCursorPhase.AfterKeyName);
    }

    public static RegistryFindCursor FromValueSelection(RegistryPath keyPath, string valueName)
    {
        ArgumentNullException.ThrowIfNull(keyPath);
        ArgumentNullException.ThrowIfNull(valueName);
        return new RegistryFindCursor(keyPath, RegistryFindCursorPhase.AfterValueData, valueName);
    }

    public static RegistryFindCursor FromHit(RegistryFindHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);

        return hit.Kind switch
        {
            RegistryFindHitKind.KeyName =>
                new RegistryFindCursor(hit.KeyPath, RegistryFindCursorPhase.AfterKeyName),
            RegistryFindHitKind.ValueName =>
                new RegistryFindCursor(hit.KeyPath, RegistryFindCursorPhase.AfterValueName, hit.ValueName!),
            RegistryFindHitKind.ValueData =>
                new RegistryFindCursor(hit.KeyPath, RegistryFindCursorPhase.AfterValueData, hit.ValueName!),
            _ => throw new ArgumentOutOfRangeException(nameof(hit)),
        };
    }
}
