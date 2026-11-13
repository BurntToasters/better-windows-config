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
}

/// <summary>A typed result. When Ok is false, Failure says why and Detail is safe to log.</summary>
public sealed record Outcome<T>(bool Ok, T? Value, UpdateFailure Failure, string Detail)
{
    public static Outcome<T> Success(T value) => new(true, value, UpdateFailure.None, string.Empty);

    public static Outcome<T> Fail(UpdateFailure failure, string detail) => new(false, default, failure, detail);
}
