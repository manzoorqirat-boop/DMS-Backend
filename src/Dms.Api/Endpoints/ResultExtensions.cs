using Dms.Application.Common;

namespace Dms.Api.Endpoints;

/// <summary>
/// The single place where an Application-layer <see cref="Error"/> becomes an HTTP status.
/// Keeping the mapping here is what lets services stay transport-agnostic and testable
/// without a web host.
/// </summary>
public static class ResultExtensions
{
    public static IResult ToHttpResult<T>(this Result<T> result) =>
        result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem();

    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult> onSuccess) =>
        result.IsSuccess ? onSuccess(result.Value) : result.Error!.ToProblem();

    public static IResult ToProblem(this Error error)
    {
        var status = error.Kind switch
        {
            ErrorKind.Validation => StatusCodes.Status400BadRequest,
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError,
        };

        return Results.Problem(
            detail: error.Message,
            statusCode: status,
            title: error.Code,
            // Surfaced in an extension member as well as the title so a client can branch on
            // the code without parsing prose that may be reworded.
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });
    }
}
