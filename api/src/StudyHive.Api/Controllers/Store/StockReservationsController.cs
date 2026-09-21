using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyHive.Api.Common;
using StudyHive.Api.Data;
using StudyHive.Api.Data.Entities;
using StudyHive.Api.Security;
using StudyHive.Api.Services;

namespace StudyHive.Api.Controllers.Store;

/// <summary>
/// S3: stock reservations. Status only ever moves Pending/Reserved -> Released or -> Used
/// (<c>stock_reservations</c> CHECK constraint — DOCS/S2_S3_S4_UI_Interface_Map.md "Reservation
/// status" note). Creating a reservation is S3's business operation beyond CRUD: it must not
/// oversell under concurrent callers, and that guarantee lives in <see cref="IConsumableStockService"/>,
/// not here — this controller only translates its result into HTTP.
/// </summary>
[ApiController]
[Route("api/stock-reservations")]
[Authorize]
public sealed class StockReservationsController(StudyHiveDbContext db, IConsumableStockService stockService) : ControllerBase
{
    /// <summary>Create a reservation transactionally. Must not oversell under concurrent callers.</summary>
    [HttpPost]
    [Authorize(Policy = "StaffOnly")]
    [ProducesResponseType(typeof(StockReservationResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CreateStockReservationRequest request, CancellationToken ct)
    {
        var result = await stockService.ReserveAsync(request.BookingRequestItemId, User.GetUserId(), ct);

        return result.Outcome switch
        {
            StockOperationOutcome.Success => CreatedAtAction(nameof(List), null, StockReservationResponse.From(result.Reservation!)),
            StockOperationOutcome.BookingRequestItemNotFound => NotFound(),
            StockOperationOutcome.AlreadyReserved => Problem(
                type: "https://studyhive.dev/errors/conflict",
                title: "Already reserved",
                statusCode: StatusCodes.Status409Conflict,
                detail: result.Detail),
            StockOperationOutcome.InsufficientStock => Problem(
                type: "https://studyhive.dev/errors/insufficient-stock",
                title: "Insufficient stock",
                statusCode: StatusCodes.Status409Conflict,
                detail: result.Detail),
            _ => Problem(statusCode: StatusCodes.Status500InternalServerError, detail: result.Detail),
        };
    }

    /// <summary>Backs W-22. `status` filters on the database's own four values — see the reservation
    /// status note in DOCS/S2_S3_S4_UI_Interface_Map.md before adding a fifth.</summary>
    [HttpGet]
    [Authorize(Roles = $"{Roles.StoreOfficer},{Roles.Librarian}")]
    [ProducesResponseType(typeof(PagedResult<StockReservationResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] PageQuery query, [FromQuery] string? status, CancellationToken ct)
    {
        IQueryable<StockReservation> reservations = db.StockReservations.AsNoTracking().Include(r => r.Consumable);

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<StockReservationStatus>(status, ignoreCase: true, out var parsedStatus))
            {
                ModelState.AddModelError(nameof(status), $"Unknown status value '{status}'.");
                return ValidationProblem(ModelState);
            }
            reservations = reservations.Where(r => r.Status == parsedStatus);
        }

        var sortDescending = !string.Equals(query.SortDir, "asc", StringComparison.OrdinalIgnoreCase);
        IOrderedQueryable<StockReservation>? sorted = query.SortBy?.ToLowerInvariant() switch
        {
            null or "" or "createdat" => sortDescending ? reservations.OrderByDescending(r => r.CreatedAt) : reservations.OrderBy(r => r.CreatedAt),
            "status" => sortDescending ? reservations.OrderByDescending(r => r.Status) : reservations.OrderBy(r => r.Status),
            _ => null,
        };
        if (sorted is null)
        {
            ModelState.AddModelError(nameof(query.SortBy), $"Unknown sortBy value '{query.SortBy}'.");
            return ValidationProblem(ModelState);
        }
        reservations = sorted;

        var totalItems = await reservations.CountAsync(ct);
        var items = await reservations
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(r => StockReservationResponse.From(r))
            .ToListAsync(ct);

        return Ok(PagedResult<StockReservationResponse>.Create(items, query.Page, query.PageSize, totalItems));
    }

    /// <summary>Release a reservation and return the stock. Only a 'Reserved' reservation can be released.</summary>
    [HttpPut("{id:guid}/release")]
    [Authorize(Roles = Roles.StoreOfficer)]
    [ProducesResponseType(typeof(StockReservationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Release(Guid id, CancellationToken ct)
    {
        var result = await stockService.ReleaseAsync(id, User.GetUserId(), ct);

        return result.Outcome switch
        {
            StockOperationOutcome.Success => Ok(StockReservationResponse.From(result.Reservation!)),
            StockOperationOutcome.ReservationNotFound => NotFound(),
            StockOperationOutcome.InvalidState => Problem(
                type: "https://studyhive.dev/errors/conflict",
                title: "Reservation cannot be released",
                statusCode: StatusCodes.Status409Conflict,
                detail: result.Detail),
            _ => Problem(statusCode: StatusCodes.Status500InternalServerError, detail: result.Detail),
        };
    }

    /// <summary>Mark a reservation as issued/used — this is when the consumable actually leaves the store.</summary>
    [HttpPut("{id:guid}/use")]
    [Authorize(Roles = Roles.StoreOfficer)]
    [ProducesResponseType(typeof(StockReservationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> MarkUsed(Guid id, CancellationToken ct)
    {
        var result = await stockService.MarkUsedAsync(id, User.GetUserId(), ct);

        return result.Outcome switch
        {
            StockOperationOutcome.Success => Ok(StockReservationResponse.From(result.Reservation!)),
            StockOperationOutcome.ReservationNotFound => NotFound(),
            StockOperationOutcome.InvalidState => Problem(
                type: "https://studyhive.dev/errors/conflict",
                title: "Reservation cannot be marked used",
                statusCode: StatusCodes.Status409Conflict,
                detail: result.Detail),
            _ => Problem(statusCode: StatusCodes.Status500InternalServerError, detail: result.Detail),
        };
    }
}
