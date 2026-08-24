namespace Dms.Application.Common;

/// <summary>
/// Thrown to abandon a unit of work that has already started a transaction.
/// <para>
/// This is the one place the codebase uses an exception for an expected outcome, and it earns
/// it: inside <c>IUnitOfWork.ExecuteInTransactionAsync</c>, returning a failed
/// <see cref="Result{T}"/> would return <i>normally</i>, and the transaction would commit —
/// silently persisting whatever had already been written, such as an advanced document number
/// counter, for a document that was never created. Throwing is what makes the rollback happen.
/// The caller catches this immediately outside the transaction and turns it back into a
/// <see cref="Result{T}"/>, so the exception never escapes the Application layer.
/// </para>
/// </summary>
public sealed class DraftAbortedException(Error error)
    : Exception(error.Message)
{
    public Error Error { get; } = error;
}
