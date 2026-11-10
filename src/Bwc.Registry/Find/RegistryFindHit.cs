namespace Bwc.Registry;

/// <summary>What part of a key or value produced a find hit.</summary>
public enum RegistryFindHitKind
{
    KeyName,
    ValueName,
    ValueData,
}

/// <summary>
/// One find stop. <see cref="ValueName"/> is null for a key-name hit, the empty string for a
/// hit on the default value, and the value's real name otherwise.
/// </summary>
public sealed class RegistryFindHit
{
    public RegistryFindHit(
        RegistryPath keyPath,
        string? valueName,
        RegistryFindHitKind kind,
        string matchedText)
    {
        ArgumentNullException.ThrowIfNull(keyPath);
        ArgumentNullException.ThrowIfNull(matchedText);

        switch (kind)
        {
            case RegistryFindHitKind.KeyName:
                if (valueName is not null)
                {
                    throw new ArgumentException(
                        "A key-name hit must not name a value.",
                        nameof(valueName));
                }

                break;
            case RegistryFindHitKind.ValueName:
            case RegistryFindHitKind.ValueData:
                if (valueName is null)
                {
                    throw new ArgumentException(
                        "A value hit must name a value. The default value name is the empty string.",
                        nameof(valueName));
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        KeyPath = keyPath;
        ValueName = valueName;
        Kind = kind;
        MatchedText = matchedText;
        DisplayText = CreateDisplayText();
    }

    public RegistryPath KeyPath { get; }

    public string? ValueName { get; }

    public RegistryFindHitKind Kind { get; }

    /// <summary>The exact text that matched, as the finder tested it.</summary>
    public string MatchedText { get; }

    /// <summary>
    /// A diagnostic rendering for logs and tests. The UI formats its own list text.
    /// </summary>
    public string DisplayText { get; }

    private string CreateDisplayText()
    {
        string key = KeyPath.ToDisplayString();

        if (Kind == RegistryFindHitKind.KeyName)
        {
            return key;
        }

        string name = RegistryValueDisplay.FormatName(ValueName!);
        return Kind == RegistryFindHitKind.ValueName
            ? $"{key} : {name}"
            : $"{key} : {name} = {MatchedText}";
    }
}
