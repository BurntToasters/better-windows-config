namespace Bwc.Update;

public enum UpdateMode
{
    Help,
    Check,
    Download,
    Apply,
    CloseRunning,
    WaitPid,
    Invalid,
}

/// <summary>
/// Parses the command line. The first argument picks the mode, and each mode accepts only its own
/// options. A value cannot start with "--", so a missing value is caught instead of taking the next
/// option name. Usage and the exit codes are in docs/contracts/update-manifest.md and update-cli.md.
/// </summary>
public sealed class UpdateArguments
{
    public const string UsageText = """
        Usage:
          BwcUpdate.exe --check --manifest-url <https url> --signature-url <https url>
                        --channel stable|beta --current-version <semver>
            Verifies the signed manifest only. Prints one JSON line. Downloads nothing.
            Exit: 0 ready, 1 refused, 2 usage.

          BwcUpdate.exe --download --manifest-url <https url> --signature-url <https url>
                        --channel stable|beta --current-version <semver>
            Verifies the manifest, then reuses or downloads and verifies the installer.
            Prints progress lines and one JSON line with stagingFolder.
            Exit: 0 staged, 1 refused, 2 usage.

          BwcUpdate.exe --apply <installer.exe> --receipt <receipt.json>
                        --current-version <semver> --install-dir <folder>
                        [--wait-pid <pid>]... [--wait-timeout-ms <ms>]
                        [--installer-switch <SWITCH[=value]>]...
            Waits for the Hub and tools to exit, verifies the installer again, and starts it
            silently (/S <switches> /D=<folder>). The installer relaunches the Hub.
            Exit: 0 installer started, 2 usage, 3 refused, 4 apps still running,
                  5 installer did not start, 6 refused and the Hub did not start.

          BwcUpdate.exe --close-running --install-dir <absolute folder>
                        [--exclude-pid <pid>]... [--timeout-seconds <1-3600>]
            Sends WM_CLOSE to BWC processes that run from the folder, then waits.
            Exit: 0 none remain, 2 usage, 3 some remain (printed as name pid=n).

          BwcUpdate.exe --wait-pid <pid> [--timeout-seconds <1-3600>]
            Waits for the process to exit. Exit: 0 exited (or never existed), 2 usage, 3 timeout.

          BwcUpdate.exe --help
        """;

    private static readonly HashSet<string> CheckValues = ["--manifest-url", "--signature-url", "--channel", "--current-version"];
    private static readonly HashSet<string> CheckRequired = ["--manifest-url", "--signature-url", "--channel", "--current-version"];

    private static readonly HashSet<string> ApplyValues =
        ["--apply", "--receipt", "--current-version", "--install-dir", "--wait-pid", "--wait-timeout-ms", "--installer-switch"];
    private static readonly HashSet<string> ApplyRequired = ["--apply", "--receipt", "--current-version", "--install-dir"];
    private static readonly HashSet<string> ApplyRepeatable = ["--wait-pid", "--installer-switch"];

    private static readonly HashSet<string> CloseRunningValues = ["--install-dir", "--exclude-pid", "--timeout-seconds"];
    private static readonly HashSet<string> CloseRunningRequired = ["--install-dir"];
    private static readonly HashSet<string> CloseRunningRepeatable = ["--exclude-pid"];

    private static readonly HashSet<string> WaitPidValues = ["--wait-pid", "--timeout-seconds"];
    private static readonly HashSet<string> WaitPidRequired = ["--wait-pid"];

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

        UpdateMode mode;
        HashSet<string> values;
        HashSet<string> required;
        HashSet<string> repeatable;
        switch (args[0])
        {
            case "--check":
                mode = UpdateMode.Check;
                values = CheckValues;
                required = CheckRequired;
                repeatable = [];
                break;
            case "--download":
                mode = UpdateMode.Download;
                values = CheckValues;
                required = CheckRequired;
                repeatable = [];
                break;
            case "--apply":
                mode = UpdateMode.Apply;
                values = ApplyValues;
                required = ApplyRequired;
                repeatable = ApplyRepeatable;
                break;
            case "--close-running":
                mode = UpdateMode.CloseRunning;
                values = CloseRunningValues;
                required = CloseRunningRequired;
                repeatable = CloseRunningRepeatable;
                break;
            case "--wait-pid":
                mode = UpdateMode.WaitPid;
                values = WaitPidValues;
                required = WaitPidRequired;
                repeatable = [];
                break;
            default:
                return Invalid("the first argument must be --check, --download, --apply, --close-running, --wait-pid, or --help");
        }

        var parsed = new UpdateArguments(mode, string.Empty);

        // Argument 0 names the mode. It is also an option of its own (--apply and --wait-pid take a value),
        // so the scan starts at 0 for those and after it for the others.
        for (var i = values.Contains(args[0]) ? 0 : 1; i < args.Count; i++)
        {
            var token = args[i];
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
            else if (!repeatable.Contains(token))
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
        switch (parsed.Mode)
        {
            case UpdateMode.Check:
            case UpdateMode.Download:
                if (!UpdateChannelNames.TryParse(parsed.Single("--channel"), out _))
                {
                    return "--channel must be stable or beta";
                }

                return SemVer.TryParse(parsed.Single("--current-version"), out _) ? null : "--current-version must be SemVer";

            case UpdateMode.CloseRunning:
                if (!Path.IsPathFullyQualified(parsed.Single("--install-dir")!))
                {
                    return "--install-dir must be an absolute folder";
                }

                foreach (var pid in parsed.Multiple("--exclude-pid"))
                {
                    if (!IsPositiveInt(pid))
                    {
                        return "--exclude-pid must be a positive whole number";
                    }
                }

                return TimeoutProblem(parsed);

            case UpdateMode.WaitPid:
                if (!IsPositiveInt(parsed.Single("--wait-pid")!))
                {
                    return "--wait-pid must be a positive whole number";
                }

                return TimeoutProblem(parsed);

            default:
                break;
        }

        if (!SemVer.TryParse(parsed.Single("--current-version"), out _))
        {
            return "--current-version must be SemVer";
        }

        foreach (var pid in parsed.Multiple("--wait-pid"))
        {
            if (!IsPositiveInt(pid))
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

    private static string? TimeoutProblem(UpdateArguments parsed)
    {
        if (parsed.Single("--timeout-seconds") is { } text
            && (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds) || seconds < 1 || seconds > 3600))
        {
            return "--timeout-seconds must be between 1 and 3600";
        }

        return null;
    }

    private static bool IsPositiveInt(string text) =>
        int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0;

    private static UpdateArguments Invalid(string error) => new(UpdateMode.Invalid, error);
}
