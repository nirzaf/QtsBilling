using QtsBilling.Contracts.Abstractions;
using QtsBilling.Contracts.Diagnostics;

namespace QtsBilling.Ui.Pages.Administration;

/// <summary>
/// What the diagnostics and audit screens may say (specification task 34). The evidence is the server's; this type
/// names it, states which question each route answers, and turns a refusal into what the operator can do next. It
/// never invents a health figure, never softens a critical warning, and never suggests that clearing a fact is the
/// way to clear a warning about it.
/// </summary>
public static class DiagnosticsRules
{
    public static readonly string EvidenceRule =
        "Every figure here was read from evidence this instance already holds: its own store, its own volume, the backup manifests on its own disk, its own terminals and change feed, and the certificate it was configured with. Nothing is fetched from a vendor, a cloud service or a support desk.";

    public static readonly string LivenessRule =
        "The anonymous health route answers one question - is the store reachable - and names nothing else. Dependencies, versions and fleet state sit behind the audit authority, because they describe a real business.";

    public static readonly string WarningRule =
        "A warning is an act to take, with the record that proves it. Deleting the audit entry, the log or the backup that raised a warning is not a fix: the facts stay under retention and the warning stays until a person answers it.";

    public static readonly string BundleRule =
        "A support bundle is written on this machine, only when an owner asks and says why. It carries no connection string, no key, no token, no password and no raw customer document, and nothing uploads it automatically.";

    public static readonly string AuditRule =
        "The audit feed is append-only. This screen reads it: an entry is never edited or removed here, and a masked field stays masked because the mask is decided where the fact was written.";

    /// <summary>The severity words a screen shows, so a warning is never dressed as a success.</summary>
    public static string SeverityWord(string severity) => severity switch
    {
        "critical" => "needs an answer now",
        "warning" => "look at it today",
        "info" => "for information",
        _ => severity,
    };

    /// <summary>Is this warning one that has to be answered before the next posting window?</summary>
    public static bool IsCritical(string severity)
        => string.Equals(severity, "critical", StringComparison.OrdinalIgnoreCase);

    public static string AlertSentence(DiagnosticsAlertDto alert)
        => $"{alert.Sentence} - {alert.Seen}. {alert.ActToTake}{(string.IsNullOrWhiteSpace(alert.Reference) ? string.Empty : $" (reference {alert.Reference})")}";

    /// <summary>Free space said the way an operator decides on it, and never rounded up into looking healthy.</summary>
    public static string CapacitySentence(CapacityDiagnosticsDto capacity)
        => capacity.Measured
            ? $"{capacity.UsedPercent:F1}% used on {capacity.Path}; {FormatBytes(capacity.FreeBytes)} free, warning at {capacity.WarnAtPercent}%"
            : $"{capacity.Path}: this instance could not measure the volume. A full disk would arrive unannounced.";

    public static string BackupSentence(BackupEvidenceDto backup)
        => !backup.Configured
            ? "No backup directory is configured, so this instance cannot prove it has a restore point."
            : backup.FileName is null
                ? $"Nothing in {backup.Directory} looks like a backup manifest."
                : backup.Stale
                    ? $"{backup.FileName} is {backup.AgeHours} hour(s) old, past the {backup.StaleAfterHours}-hour window. {backup.RestoreVerificationNote}."
                    : $"{backup.FileName} ({FormatBytes(backup.SizeBytes ?? 0)}, sha256 {Shorten(backup.Sha256)}) is {backup.AgeHours} hour(s) old. {backup.RestoreVerificationNote}.";

    public static string StoreSentence(StoreDiagnosticsDto store)
        => store.Reachable
            ? $"{store.DatabaseVersion}, schema {store.SchemaVersion} from {store.AppliedMigrations} migration(s); the probe took {store.ProbeMilliseconds} ms"
            : "the authoritative store did not answer";

    public static string KeySentence(KeyExpiryDiagnosticsDto key)
        => !key.Configured
            ? "no signing certificate is configured"
            : key.DaysRemaining is { } days
                ? $"{Shorten(key.Subject)} expires in {days} day(s)"
                : key.Note;

    public static string SyncSentence(SyncDiagnosticsDto sync)
        => $"revision {sync.CurrentRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)} with {sync.ChangesInFeed} published change(s); {sync.Accepted} capture(s) posted, {sync.Duplicates} answered as already posted, {sync.Rejected} refused or needing review";

    public static string DeviceSentence(DeviceFleetDiagnosticsDto devices)
        => $"{devices.Active} active, {devices.Revoked} revoked, {devices.Retired} retired, {devices.Silent} silent for over {devices.SilentAfterHours} hour(s), {devices.BehindOnSchema} behind on schema";

    /// <summary>The act a bundle needs before it is written: a reason a reader will find inside the file.</summary>
    public static bool CanRequestBundle(string? reason, bool busy)
        => !busy && reason is { Length: >= 5 } && reason.Length <= 500;

    public static string RefusalSentence(ApiException exception, string what) => exception.Code switch
    {
        ApiErrorCodes.StoreUnavailable =>
            $"'{what}' could not be completed on this instance: {exception.Message}. Nothing was claimed as written.",
        ApiErrorCodes.ValidationFailed =>
            $"'{what}' was not accepted as it stands: {exception.Message}",
        ApiErrorCodes.Forbidden =>
            $"'{what}' needs the audit authority, which this account does not hold here.",
        ApiErrorCodes.NotFound =>
            $"'{what}' named something this business does not hold, so nothing was read.",
        _ => $"'{what}' could not be confirmed: {exception.Message}. Nothing was assumed about the outcome.",
    };

    /// <summary>Only refusals that prove nothing was written let the button be pressed again freely.</summary>
    public static bool CommittedNothing(string? code) => code is ApiErrorCodes.ValidationFailed
        or ApiErrorCodes.StoreUnavailable
        or ApiErrorCodes.Forbidden
        or ApiErrorCodes.NotFound
        or ApiErrorCodes.Conflict;

    private static string FormatBytes(long bytes)
        => bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824d:F1} GB",
            >= 1_048_576 => $"{bytes / 1_048_576d:F1} MB",
            >= 1_024 => $"{bytes / 1_024d:F1} KB",
            _ => $"{bytes} B",
        };

    private static string Shorten(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "not recorded";
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 48 ? trimmed : trimmed[..48] + "...";
    }
}
