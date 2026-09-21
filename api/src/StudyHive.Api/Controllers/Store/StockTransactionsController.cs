using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyHive.Api.Common;
using StudyHive.Api.Data;
using StudyHive.Api.Data.Entities;

namespace StudyHive.Api.Controllers.Store;

/// <summary>S3: the append-only stock ledger. Every row is written by <see cref="StudyHive.Api.Services.IConsumableStockService"/>
/// alongside the counter it explains (DOCS §07: "balance_after makes it reconcilable") — this
/// controller is read-only, on purpose: the ledger has no PUT or DELETE.</summary>
[ApiController]
[Route("api/stock-transactions")]
[Authorize]
public sealed class StockTransactionsController(StudyHiveDbContext db) : ControllerBase
{
    /// <summary>Transaction history, filterable by consumable, with sort and pagination.</summary>
    [HttpGet]
    [Authorize(Roles = Roles.StoreOfficer)]
    [ProducesResponseType(typeof(PagedResult<StockTransactionResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] PageQuery query, [FromQuery] Guid? consumableId, CancellationToken ct)
    {
        IQueryable<StockTransaction> transactions = db.StockTransactions.AsNoTracking();

        if (consumableId is not null)
        {
            transactions = transactions.Where(t => t.ConsumableId == consumableId);
        }

        var sortDescending = !string.Equals(query.SortDir, "asc", StringComparison.OrdinalIgnoreCase);
        IOrderedQueryable<StockTransaction>? sorted = query.SortBy?.ToLowerInvariant() switch
        {
            null or "" or "createdat" => sortDescending ? transactions.OrderByDescending(t => t.CreatedAt) : transactions.OrderBy(t => t.CreatedAt),
            "transactiontype" => sortDescending ? transactions.OrderByDescending(t => t.TransactionType) : transactions.OrderBy(t => t.TransactionType),
            _ => null,
        };
        if (sorted is null)
        {
            ModelState.AddModelError(nameof(query.SortBy), $"Unknown sortBy value '{query.SortBy}'.");
            return ValidationProblem(ModelState);
        }
        transactions = sorted;

        var totalItems = await transactions.CountAsync(ct);
        var items = await transactions
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(t => StockTransactionResponse.From(t))
            .ToListAsync(ct);

        return Ok(PagedResult<StockTransactionResponse>.Create(items, query.Page, query.PageSize, totalItems));
    }
}
