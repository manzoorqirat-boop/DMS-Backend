namespace Dms.Application.Common;

/// <summary>
/// A page request. Clamped rather than validated: an out-of-range page size is far more likely
/// a typo or a stale client than an attack, and silently returning a sane page beats a 400 that
/// a frontend has to special-case.
/// </summary>
public sealed record PagedRequest
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public PagedRequest(int? page = null, int? pageSize = null)
    {
        Page = Math.Max(1, page ?? 1);
        PageSize = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
    }

    /// <summary>1-based. Page numbers a user sees start at 1, so the API's do too.</summary>
    public int Page { get; }

    public int PageSize { get; }

    public int Skip => (Page - 1) * PageSize;
}

/// <summary>
/// One page of results plus enough context to render a pager.
/// <para>
/// <see cref="TotalCount"/> costs a second query on every request. Worth it here: the master
/// register and the audit trail are things people scan and cite positions in ("row 412 of
/// 3,908"), and a register that can't tell you how many controlled documents exist is a poor
/// register.
/// </para>
/// </summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public int TotalPages => TotalCount == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasNextPage => Page < TotalPages;

    public static PagedResult<T> Empty(PagedRequest request) =>
        new([], request.Page, request.PageSize, 0);

    /// <summary>Projects the items while keeping the paging envelope intact.</summary>
    public PagedResult<TOut> Map<TOut>(Func<T, TOut> selector) =>
        new(Items.Select(selector).ToList(), Page, PageSize, TotalCount);
}
