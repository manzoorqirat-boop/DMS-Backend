namespace Dms.Application.Common;

/// <summary>
/// Failure classification, mapped to an HTTP status only at the API edge so the Application
/// layer stays transport-agnostic.
/// </summary>
public enum ErrorKind
{
    /// <summary>Caller sent something malformed or disallowed. 400.</summary>
    Validation,

    /// <summary>Referenced entity doesn't exist. 404.</summary>
    NotFound,

    /// <summary>Well-formed request that collides with current state. 409.</summary>
    Conflict,
}

/// <summary>
/// A failure carrying a stable machine-readable <see cref="Code"/>. The code is what a
/// frontend or an integration test branches on; <see cref="Message"/> is for humans and may
/// be reworded freely without breaking callers.
/// </summary>
public sealed record Error(ErrorKind Kind, string Code, string Message)
{
    public static Error Validation(string code, string message) => new(ErrorKind.Validation, code, message);

    public static Error NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);

    public static Error Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);
}

/// <summary>
/// Success-or-failure without exceptions-as-control-flow. Expected outcomes ("that document
/// type doesn't exist", "a concurrent upload took that version number") are values;
/// exceptions stay reserved for genuine faults.
/// </summary>
public readonly struct Result<T>
{
    private readonly T? _value;

    private Result(T value)
    {
        _value = value;
        Error = null;
    }

    private Result(Error error)
    {
        _value = default;
        Error = error;
    }

    public Error? Error { get; }

    public bool IsSuccess => Error is null;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot read Value from a failed Result.");

    public static Result<T> Success(T value) => new(value);

    public static Result<T> Failure(Error error) => new(error);

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure(error);
}
