namespace Bwc.Update;

public enum UpdateMode
{
    Help,
    Check,
    Apply,
    Invalid,
}

/// <summary>
/// Parses the command line. Each mode accepts only its own options. A value cannot start with
/// "--", so a missing value is caught instead of taking the next option name.
/// </summary>
public sealed class UpdateArguments
{
    public const string UsageText = """
        Usage:
          BwcUpdate.exe --check --manifest-url <https url> --signature-url <https url>
                        --channel stable|beta --current-version <semver>
            Downloads and verifies the manifest and installer. Prints one JSON line.
            Exit: 0 ready, 1 refused, 2 usage.

          BwcUpdate.exe --apply <installer.exe> --receipt <receipt.json>
                        --current-version <semver> --install-dir <folder>
                        [--wait-pid <pid>]... [--wait-timeout-ms <ms>]
                        [--installer-switch <SWITCH>]...
            Waits for the Hub and tools to exit, verifies the installer again, runs it
            silently (/S /D=<folder>), and starts the Hub again.
            Exit: 0 installed, 2 usage, 3 refused, 4 apps still running,
                  5 installer failed or timed out, 6 installed but the Hub did not start.

          BwcUpdate.exe --help
        """;

    private static readonly HashSet<string> CheckValues = ["--manifest-url", "--signature-url", "--channel", "--current-version"];
    private static readonly HashSet<string> CheckRequired = ["--manifest-url", "--signature-url", "--channel", "--current-version"];
    private static readonly HashSet<string> CheckFlags = ["--check"];

    private static readonly HashSet<string> ApplyValues =
        ["--apply", "--receipt", "--current-version", "--install-dir", "--wait-pid", "--wait-timeout-ms", "--installer-switch"];
    private static readonly HashSet<string> ApplyRequired = ["--apply", "--receipt", "--current-version", "--install-dir"];
    private static readonly HashSet<string> ApplyFlags = ["--relocated"];

    private static readonly HashSet<string> Repeatable = ["--wait-pid", "--installer-switch"];

    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    private UpdateArguments(UpdateMode mode, string error)
    {
        Mode = mode;
        Error = error;
    }

    public UpdateMode Mode { get; }

    /// <summary>Empty unless Mode is Invalid.</summary>
    public string Error { get; }

    public string? Single(string name) =>
        _values.TryGetValue(name, out var list) && list.Count > 0 ? list[0] : null;

    public IReadOnlyList<string> Multiple(string name) =>
        _values.TryGetValue(name, out var list) ? list : [];

    public bool Has(string flag) => _flags.Contains(flag);

    public static UpdateArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0 || args[0] is "--help" or "-h")
        {
            return new UpdateArguments(UpdateMode.Help, string.Empty);
        }

        HashSet<string> values;
        HashSet<string> flags;
        HashSet<string> required;
        switch (args[0])
        {
            case "--check":
                values = CheckValues;
                flags = CheckFlags;
                required = CheckRequired;
                break;
            case "--apply":
                values = ApplyValues;
                flags = ApplyFlags;
                required = ApplyRequired;
                break;
            default:
                return Invalid("the first argument must be --check, --apply, or --help");
        }

        var parsed = new UpdateArguments(args[0] == "--check" ? UpdateMode.Check : UpdateMode.Apply, string.Empty);
        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (flags.Contains(token))
            {
                if (!parsed._flags.Add(token))
                {
                    return Invalid($"option {token} is given twice");
                }

                continue;
            }

            if (!values.Contains(token))
            {
                return Invalid($"unknown option {token}");
            }

            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return Invalid($"option {token} needs a value");
            }

            var value = args[++i];
            if (!parsed._values.TryGetValue(token, out var list))
            {
                list = [];
                parsed._values[token] = list;
            }
            else if (!Repeatable.Contains(token))
            {
                return Invalid($"option {token} is given twice");
            }

            list.Add(value);
        }

        foreach (var name in required)
        {
            if (!parsed._values.ContainsKey(name))
            {
                return Invalid($"missing required option {name}");
            }
        }

        var problem = ValidateValues(parsed);
        return problem is null ? parsed : Invalid(problem);
    }

    private static string? ValidateValues(UpdateArguments parsed)
    {
        if (parsed.Mode == UpdateMode.Check)
        {
            if (!UpdateChannelNames.TryParse(parsed.Single("--channel"), out _))
            {
                return "--channel must be stable or beta";
            }

            return SemVer.TryParse(parsed.Single("--current-version"), out _) ? null : "--current-version must be SemVer";
        }

        if (!SemVer.TryParse(parsed.Single("--current-version"), out _))
        {
            return "--current-version must be SemVer";
        }

        foreach (var pid in parsed.Multiple("--wait-pid"))
        {
            if (!int.TryParse(pid, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) || value <= 0)
            {
                return "--wait-pid must be a positive whole number";
            }
        }

        if (parsed.Single("--wait-timeout-ms") is { } timeout
            && (!int.TryParse(timeout, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ms) || ms <= 0 || ms > 600_000))
        {
            return "--wait-timeout-ms must be between 1 and 600000";
        }

        return null;
    }

    private static UpdateArguments Invalid(string error) => new(UpdateMode.Invalid, error);
}
