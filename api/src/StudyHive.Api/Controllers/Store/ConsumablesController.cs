using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyHive.Api.Common;
using StudyHive.Api.Data;
using StudyHive.Api.Data.Entities;
using StudyHive.Api.Security;
using StudyHive.Api.Services;

namespace StudyHive.Api.Controllers.Store;

/// <summary>S3: the consumables catalogue and its stock ledger. See DOCS §11 API table.</summary>
[ApiController]
[Route("api/consumables")]
[Authorize]
public sealed class ConsumablesController(StudyHiveDbContext db, IConsumableStockService stockService) : ControllerBase
{
    [HttpPost]
    [Authorize(Roles = $"{Roles.StoreOfficer},{Roles.Admin}")]
    [ProducesResponseType(typeof(ConsumableResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CreateConsumableRequest request, CancellationToken ct)
    {
        var nameTaken = await db.Consumables.AsNoTracking().AnyAsync(c => c.Name == request.Name.Trim(), ct);
        if (nameTaken)
        {
            return Problem(
                type: "https://studyhive.dev/errors/conflict",
                title: "Consumable name already exists",
                statusCode: StatusCodes.Status409Conflict,
                detail: $"A consumable named '{request.Name}' already exists.");
        }

        var consumable = new Consumable
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            Unit = request.Unit.Trim(),
            UnitPrice = request.UnitPrice,
            StockQuantity = request.StockQuantity,
            MinStockLevel = request.MinStockLevel,
        };

        db.Consumables.Add(consumable);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetById), new { id = consumable.Id }, ConsumableResponse.From(consumable));
    }

    /// <summary>Backs W-19: search, filter, sort, paginate.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<ConsumableResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] PageQuery query, [FromQuery] bool activeOnly = true, CancellationToken ct = default)
    {
        IQueryable<Consumable> consumables = db.Consumables.AsNoTracking();

        if (activeOnly)
        {
            consumables = consumables.Where(c => c.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = $"%{query.Search.Trim()}%";
            consumables = consumables.Where(c => EF.Functions.ILike(c.Name, search));
        }

        var sortDescending = string.Equals(query.SortDir, "desc", StringComparison.OrdinalIgnoreCase);
        IOrderedQueryable<Consumable>? sorted = query.SortBy?.ToLowerInvariant() switch
        {
            null or "" or "name" => sortDescending ? consumables.OrderByDescending(c => c.Name) : consumables.OrderBy(c => c.Name),
            "unitprice" => sortDescending ? consumables.OrderByDescending(c => c.UnitPrice) : consumables.OrderBy(c => c.UnitPrice),
            "stockquantity" => sortDescending ? consumables.OrderByDescending(c => c.StockQuantity) : consumables.OrderBy(c => c.StockQuantity),
            "createdat" => sortDescending ? consumables.OrderByDescending(c => c.CreatedAt) : consumables.OrderBy(c => c.CreatedAt),
            _ => null,
        };
        if (sorted is null)
        {
            ModelState.AddModelError(nameof(query.SortBy), $"Unknown sortBy value '{query.SortBy}'.");
            return ValidationProblem(ModelState);
        }
        consumables = sorted;

        var totalItems = await consumables.CountAsync(ct);
        var items = await consumables
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(c => ConsumableResponse.From(c))
            .ToListAsync(ct);

        return Ok(PagedResult<ConsumableResponse>.Create(items, query.Page, query.PageSize, totalItems));
    }

    /// <summary>Backs W-21 and the low-stock email to the store officer. Mirrors the partial index
    /// <c>ix_cons_low</c> (S3Configurations.cs) exactly, so this list is always what that index covers.</summary>
    [HttpGet("low-stock")]
    [Authorize(Roles = $"{Roles.StoreOfficer},{Roles.Librarian}")]
    [ProducesResponseType(typeof(IReadOnlyList<ConsumableResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> LowStock(CancellationToken ct)
    {
        var items = await db.Consumables.AsNoTracking()
            .Where(c => c.IsActive && c.StockQuantity <= c.MinStockLevel)
            .OrderBy(c => c.StockQuantity)
            .Select(c => ConsumableResponse.From(c))
            .ToListAsync(ct);

        return Ok(items);
    }

    /// <summary>Backs W-20: detail plus its recent ledger entries.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ConsumableDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var consumable = await db.Consumables.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
        if (consumable is null) return NotFound();

        var recentTransactions = await db.StockTransactions.AsNoTracking()
            .Where(t => t.ConsumableId == id)
            .OrderByDescending(t => t.CreatedAt)
            .Take(20)
            .Select(t => StockTransactionResponse.From(t))
            .ToListAsync(ct);

        return Ok(new ConsumableDetailResponse
        {
            Consumable = ConsumableResponse.From(consumable),
            RecentTransactions = recentTransactions,
        });
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = Roles.StoreOfficer)]
    [ProducesResponseType(typeof(ConsumableResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid id, UpdateConsumableRequest request, CancellationToken ct)
    {
        var consumable = await db.Consumables.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (consumable is null) return NotFound();

        var nameTaken = await db.Consumables.AsNoTracking()
            .AnyAsync(c => c.Id != id && c.Name == request.Name.Trim(), ct);
        if (nameTaken)
        {
            return Problem(
                type: "https://studyhive.dev/errors/conflict",
                title: "Consumable name already exists",
                statusCode: StatusCodes.Status409Conflict,
                detail: $"A consumable named '{request.Name}' already exists.");
        }

        // Stock counts are never edited here — they only move through /stock-in and the
        // reservation lifecycle, each of which writes a matching stock_transactions row. A plain
        // PUT that could silently change stock_quantity would break the ledger's reconcilability.
        consumable.Name = request.Name.Trim();
        consumable.Description = request.Description?.Trim();
        consumable.Unit = request.Unit.Trim();
        consumable.UnitPrice = request.UnitPrice;
        consumable.MinStockLevel = request.MinStockLevel;
        consumable.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Ok(ConsumableResponse.From(consumable));
    }

    /// <summary>Deactivate — never a physical delete (DOCS shared conventions).</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "AdminOnly")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var consumable = await db.Consumables.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (consumable is null) return NotFound();

        consumable.IsActive = false;
        consumable.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    /// <summary>Add stock. A business operation, not CRUD: it writes a stock_transactions row as
    /// well as moving the balance (DOCS §11).</summary>
    [HttpPost("{id:guid}/stock-in")]
    [Authorize(Roles = Roles.StoreOfficer)]
    [ProducesResponseType(typeof(ConsumableResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> StockIn(Guid id, StockInRequest request, CancellationToken ct)
    {
        var result = await stockService.StockInAsync(id, request.Quantity, User.GetUserId(), request.Notes?.Trim(), ct);

        if (result.Outcome == StockOperationOutcome.ConsumableNotFound) return NotFound();

        return Ok(ConsumableResponse.From(result.Consumable!));
    }
}
