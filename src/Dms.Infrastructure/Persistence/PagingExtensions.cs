using Dms.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace Dms.Infrastructure.Persistence;

internal static class PagingExtensions
{
    /// <summary>
    /// Counts, then fetches one page. The count runs first so a page beyond the end still
    /// reports the real total — otherwise a client that over-scrolled would be told there is
    /// nothing at all rather than that it went too far.
    /// </summary>
    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> query,
        PagedRequest request,
        CancellationToken cancellationToken)
    {
        var total = await query.CountAsync(cancellationToken);

        if (total == 0)
        {
            return PagedResult<T>.Empty(request);
        }

        var items = await query
            .Skip(request.Skip)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<T>(items, request.Page, request.PageSize, total);
    }
}
