using System.Diagnostics.CodeAnalysis;

namespace TriggerHelper.Loading;

internal readonly struct Result<TValue, TError>
{
    private readonly TValue _value;
    private readonly TError _error;

    public bool IsSuccess { get; }

    private Result(TValue value)
    {
        _value = value;
        _error = default!;
        IsSuccess = true;
    }

    private Result(TError error)
    {
        _value = default!;
        _error = error;
        IsSuccess = false;
    }

    public static Result<TValue, TError> Ok(TValue value) => new(value);

    public static Result<TValue, TError> Fail(TError error) => new(error);

    public bool TryGetValue([MaybeNullWhen(false)] out TValue value)
    {
        value = IsSuccess ? _value : default;
        return IsSuccess;
    }

    public bool TryGetError([MaybeNullWhen(false)] out TError error)
    {
        error = !IsSuccess ? _error : default;
        return !IsSuccess;
    }
}
