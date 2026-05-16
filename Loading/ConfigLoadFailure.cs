namespace TriggerHelper.Loading;

internal readonly record struct ConfigLoadFailure
(
    ConfigLoadFailureKind Kind,
    string Path,
    string? Details
);
