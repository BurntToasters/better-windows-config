namespace Bwc.Update;

public enum UpdateMode
{
    Help,
    Check,
    Download,
    Apply,
    CloseRunning,
    WaitPid,
    ApplyElevated,
    LaunchHub,
    SetAumid,
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
            silently (/S /RELAUNCH /PARENTPID=<this pid> <switches> /D=<folder>). The installer relaunches the Hub.
            Exit: 0 installer started, 2 usage, 3 refused, 4 apps still running,
                  5 installer did not start, 6 refused and the Hub did not start.

          BwcUpdate.exe --apply-elevated <staging folder>
            Runs elevated, started by --apply. Needs an elevated token (NotElevated otherwise). The install dir is this
            program's own folder and the current version is its own version: no other option exists. Copies the
            installer into <install dir>.upd<32 hex>, verifies the copy, and starts it
            (/S /RELAUNCH /PARENTPID=<this pid> /D=<install dir>). Writes no log file.
            Exit: 0 installer started, 2 usage, 3 refused, 5 installer did not start,
                  6 refused and the Hub did not start, 7 unexpected.

          BwcUpdate.exe --launch-hub [--update-result <UpdateFailure>]
            Starts BetterWindowsConfig.exe from this program's folder at the shell's integrity level.
            Exit: 0 started, 2 usage, 6 no Hub exe or it did not start, 7 unexpected,
                  8 elevated and the Hub cannot be started unelevated (nothing was started).

          BwcUpdate.exe --set-aumid <absolute path of a .lnk> <app id>
            Sets System.AppUserModel.ID on one of the two Start menu shortcuts of this product (app id
            BetterWindowsConfig.Hub for "Better Windows Config.lnk", BetterWindowsConfig.Regedit for "Registry Editor (BWC).lnk"),
            directly in the all-users Programs folder, and reads it back. Run by the installer. Writes no log when elevated.
            Exit: 0 written, 2 usage, 3 refused (another folder, name, or a link), 4 not written, 7 unexpected.

          BwcUpdate.exe --close-running --install-dir <absolute folder>
                        [--exclude-pid <pid>]... [--timeout-seconds <1-3600>]
            Sends WM_CLOSE to BWC processes that run from the folder, then waits.
            A BWC process from the folder in another session fails first, before anything is closed:
            printed as RunningInOtherSession session=<id> <name> pid=<n>.
            Exit: 0 none remain, 2 usage, 3 some remain (printed as name pid=n), 4 running in another session.

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

    private static readonly HashSet<string> ApplyElevatedValues = ["--apply-elevated"];

    private static readonly HashSet<string> LaunchHubValues = ["--update-result"];

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

        if (args[0] == "--set-aumid")
        {
            return ParseSetAumid(args);
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
            case "--apply-elevated":
                mode = UpdateMode.ApplyElevated;
                values = ApplyElevatedValues;
                required = ApplyElevatedValues;
                repeatable = [];
                break;
            case "--launch-hub":
                mode = UpdateMode.LaunchHub;
                values = LaunchHubValues;
                required = [];
                repeatable = [];
                break;
            default:
                return Invalid(
                    "the first argument must be --check, --download, --apply, --apply-elevated, --launch-hub, --set-aumid, --close-running, --wait-pid, or --help");
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

    /// <summary>
    /// <c>--set-aumid &lt;shortcut&gt; &lt;app id&gt;</c> is positional: exactly two values, neither of them
    /// option-like. The path and id
    /// rules are <see cref="ShortcutAumidRules.ArgumentProblem"/>.
    /// </summary>
    private static UpdateArguments ParseSetAumid(IReadOnlyList<string> args)
    {
        if (args.Count != 3 || args[1].StartsWith("--", StringComparison.Ordinal) || args[2].StartsWith("--", StringComparison.Ordinal))
        {
            return Invalid("--set-aumid needs a shortcut path and an app id, and nothing else");
        }

        var problem = ShortcutAumidRules.ArgumentProblem(args[1], args[2]);
        if (problem is not null)
        {
            return Invalid(problem);
        }

        var parsed = new UpdateArguments(UpdateMode.SetAumid, string.Empty);
        parsed._values["--set-aumid"] = [args[1]];
        parsed._values["--app-id"] = [args[2]];
        return parsed;
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

            case UpdateMode.ApplyElevated:
                return IsCleanAbsolutePath(parsed.Single("--apply-elevated")!) ? null : "--apply-elevated must be an absolute folder";

            case UpdateMode.LaunchHub:
                return parsed.Single("--update-result") is { } result && !TryParseUpdateResult(result, out _)
                    ? "--update-result must be the name of an UpdateFailure other than None"
                    : null;

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

    /// <summary>
    /// True when <paramref name="text"/> is exactly the name of an <see cref="UpdateFailure"/> other than None. A number, a
    /// different spelling, or text with a space or quote is not accepted, so it is safe on a command line.
    /// </summary>
    public static bool TryParseUpdateResult(string? text, out UpdateFailure failure)
    {
        failure = UpdateFailure.None;
        if (string.IsNullOrEmpty(text) || !Enum.TryParse(text, ignoreCase: false, out UpdateFailure parsed)
            || parsed == UpdateFailure.None || !string.Equals(parsed.ToString(), text, StringComparison.Ordinal))
        {
            return false;
        }

        failure = parsed;
        return true;
    }

    private static bool IsCleanAbsolutePath(string text)
    {
        if (!Path.IsPathFullyQualified(text))
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c == '"' || c < 0x20)
            {
                return false;
            }
        }

        return true;
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
