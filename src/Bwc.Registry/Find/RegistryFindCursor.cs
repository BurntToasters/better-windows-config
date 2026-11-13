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
/// the contract's "start after the current selection" rule. If the key or value named here no
/// longer exists, the finder resumes at the nearest later position in the classic NLS order.
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

            // Classic reports a matching value once even when both its name and its data
            // contain the needle. Find Next therefore resumes after the whole value.
            RegistryFindHitKind.ValueName =>
                new RegistryFindCursor(hit.KeyPath, RegistryFindCursorPhase.AfterValueData, hit.ValueName!),
            RegistryFindHitKind.ValueData =>
                new RegistryFindCursor(hit.KeyPath, RegistryFindCursorPhase.AfterValueData, hit.ValueName!),
            _ => throw new ArgumentOutOfRangeException(nameof(hit)),
        };
    }
}
