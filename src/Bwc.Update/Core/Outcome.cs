namespace Bwc.Update;

/// <summary>Every refusal the updater can give. Check and apply share this list.</summary>
public enum UpdateFailure
{
    None,
    InvalidOptions,
    TrustedKeysInvalid,
    TrustedKeysNotConfigured,
    UrlRejected,
    DownloadFailed,
    Timeout,
    Canceled,
    Io,
    ManifestDownload,
    ManifestTooLarge,
    ManifestSignatureDownload,
    ManifestSignatureInvalid,
    ManifestInvalid,
    UnsupportedSchema,
    ChannelMismatch,
    PrereleaseOnStable,
    ManifestDatedInFuture,
    ManifestExpired,
    SameVersion,
    Downgrade,
    NoInstallerForArchitecture,
    InstallerDownload,
    InstallerDownloadInterrupted,
    InstallerTooLarge,
    InstallerSizeMismatch,
    InstallerHashMismatch,
    InstallerSignatureInvalid,
    InstallerCommentMismatch,
    ReceiptInvalid,
    PathRejected,
    ArchitectureMismatch,
    ProcessesStillRunning,
    InstallDirUnsafe,
    InstallerArgumentRejected,
    InstallerFailed,
    HubMissing,
    RunningElevated,

    /// <summary>The consent prompt for the elevated helper was declined (Win32 error 1223).</summary>
    ElevationDeclined,

    /// <summary><c>--apply-elevated</c> ran without an elevated token.</summary>
    NotElevated,

    /// <summary>A BWC process from the install folder runs in another session.</summary>
    RunningInOtherSession,

    /// <summary>The Hub cannot be started at the shell's integrity level, so it was not started.</summary>
    DeElevationFailed,
    Unexpected,
}

/// <summary>A typed result. When Ok is false, Failure says why and Detail is safe to log.</summary>
public sealed record Outcome<T>(bool Ok, T? Value, UpdateFailure Failure, string Detail)
{
    public static Outcome<T> Success(T value) => new(true, value, UpdateFailure.None, string.Empty);

    public static Outcome<T> Fail(UpdateFailure failure, string detail) => new(false, default, failure, detail);
}
