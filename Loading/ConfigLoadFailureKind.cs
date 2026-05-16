namespace TriggerHelper.Loading;

internal enum ConfigLoadFailureKind
{
    NotFound,

    ReadFailed,

    InvalidJson,

    EmptyContent,
}
