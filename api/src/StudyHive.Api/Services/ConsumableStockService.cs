using Microsoft.EntityFrameworkCore;
using Npgsql;
using StudyHive.Api.Data;
using StudyHive.Api.Data.Entities;

namespace StudyHive.Api.Services;

public enum StockOperationOutcome
{
    Success,
    ConsumableNotFound,
    ReservationNotFound,
    BookingRequestItemNotFound,
    AlreadyReserved,
    InvalidState,
    InsufficientStock,
}

public sealed class StockOperationResult
{
    public required StockOperationOutcome Outcome { get; init; }
    public StockReservation? Reservation { get; init; }
    public Consumable? Consumable { get; init; }
    public string? Detail { get; init; }

    public bool Succeeded => Outcome == StockOperationOutcome.Success;

    public static StockOperationResult Success(StockReservation? reservation = null, Consumable? consumable = null) =>
        new() { Outcome = StockOperationOutcome.Success, Reservation = reservation, Consumable = consumable };

    public static StockOperationResult Failure(StockOperationOutcome outcome, string detail) =>
        new() { Outcome = outcome, Detail = detail };
}

/// <summary>
/// S3's one business operation beyond CRUD (DOCS §11 / §03 ownership table): "Transactional stock
/// reservation — atomically reserve consumables using a database transaction. If available stock
/// &lt; requested quantity, the transaction rolls back."
///
/// The concurrency guarantee is not application code — it is the <c>chk_never_oversold</c> CHECK
/// constraint on <c>consumables</c> (reserved_quantity &lt;= stock_quantity), enforced by Postgres
/// itself. Every mutation here goes through a single guarded UPDATE statement inside an explicit
/// transaction (DOCS §07: "Reserving is one statement inside a transaction ... raises 23514 if it
/// would exceed stock_quantity"), so two concurrent reservations for the last unit of stock cannot
/// both succeed: the database serializes the two UPDATEs, and whichever commits second sees a
/// constraint violation and rolls back. That is the exact behaviour the required concurrency test
/// has to demonstrate.
/// </summary>
public interface IConsumableStockService
{
    Task<StockOperationResult> StockInAsync(Guid consumableId, int quantity, Guid createdByUserId, string? notes, CancellationToken ct);

    /// <summary>Records intent only — writes a Pending row, never touches reserved_quantity. Called
    /// by <see cref="WorkflowOrchestrationService"/> when it persists the real Resource agent's
    /// output (DOCS §11: "Creates Pending reservation records but does not actually reserve stock").</summary>
    Task<StockOperationResult> CreatePendingReservationAsync(Guid bookingRequestItemId, CancellationToken ct);

    Task<StockOperationResult> ReserveAsync(Guid bookingRequestItemId, Guid performedByUserId, CancellationToken ct);

    Task<StockOperationResult> ReleaseAsync(Guid reservationId, Guid performedByUserId, CancellationToken ct);

    Task<StockOperationResult> MarkUsedAsync(Guid reservationId, Guid performedByUserId, CancellationToken ct);
}

public sealed class ConsumableStockService(StudyHiveDbContext db) : IConsumableStockService
{
    public async Task<StockOperationResult> StockInAsync(Guid consumableId, int quantity, Guid createdByUserId, string? notes, CancellationToken ct)
    {
        var exists = await db.Consumables.AsNoTracking().AnyAsync(c => c.Id == consumableId, ct);
        if (!exists) return StockOperationResult.Failure(StockOperationOutcome.ConsumableNotFound, "Consumable not found.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // stock_quantity only ever grows here — chk_consumables_stock_quantity (>= 0) can never fire,
        // but the statement stays guarded and inside a transaction with the ledger write for the same
        // reason every other mutation in this service is: one row, one truth, always together.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE consumables SET stock_quantity = stock_quantity + {quantity}, updated_at = now() WHERE id = {consumableId}", ct);

        var consumable = await db.Consumables.AsNoTracking().SingleAsync(c => c.Id == consumableId, ct);

        db.StockTransactions.Add(new StockTransaction
        {
            Id = Guid.NewGuid(),
            ConsumableId = consumableId,
            TransactionType = StockTransactionType.StockIn,
            Quantity = quantity,
            BalanceAfter = consumable.StockQuantity,
            Notes = notes,
            CreatedBy = createdByUserId,
        });
        await db.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);
        return StockOperationResult.Success(consumable: consumable);
    }

    public async Task<StockOperationResult> CreatePendingReservationAsync(Guid bookingRequestItemId, CancellationToken ct)
    {
        var item = await db.BookingRequestItems.AsNoTracking()
            .SingleOrDefaultAsync(i => i.Id == bookingRequestItemId, ct);
        if (item is null)
        {
            return StockOperationResult.Failure(StockOperationOutcome.BookingRequestItemNotFound, "Booking request item not found.");
        }

        var alreadyExists = await db.StockReservations.AsNoTracking()
            .AnyAsync(r => r.BookingRequestItemId == bookingRequestItemId, ct);
        if (alreadyExists)
        {
            return StockOperationResult.Failure(StockOperationOutcome.AlreadyReserved, "This booking request item already has a stock reservation.");
        }

        // Deliberately no transaction, no guarded UPDATE, no ledger row: a Pending record is only
        // ever a note of intent for the librarian to review before approving — reserved_quantity
        // does not move until ReserveAsync runs the real, guarded transition below.
        var reservation = new StockReservation
        {
            Id = Guid.NewGuid(),
            BookingRequestItemId = bookingRequestItemId,
            ConsumableId = item.ConsumableId,
            Quantity = item.Quantity,
            Status = StockReservationStatus.Pending,
        };
        db.StockReservations.Add(reservation);
        await db.SaveChangesAsync(ct);

        var withConsumable = await db.StockReservations.AsNoTracking().Include(r => r.Consumable)
            .SingleAsync(r => r.Id == reservation.Id, ct);
        return StockOperationResult.Success(withConsumable);
    }

    public async Task<StockOperationResult> ReserveAsync(Guid bookingRequestItemId, Guid performedByUserId, CancellationToken ct)
    {
        var item = await db.BookingRequestItems.AsNoTracking()
            .SingleOrDefaultAsync(i => i.Id == bookingRequestItemId, ct);
        if (item is null)
        {
            return StockOperationResult.Failure(StockOperationOutcome.BookingRequestItemNotFound, "Booking request item not found.");
        }

        // A Pending row (created earlier by CreatePendingReservationAsync, during the Resource
        // agent's workflow step) is promoted in place, not replaced — BookingRequestItemId is
        // unique, so inserting a second row here would just violate that constraint. No existing
        // row at all is an equally valid path (e.g. a reservation created directly, bypassing the
        // Pending stage, which every test in this service's test suite does).
        var existing = await db.StockReservations.SingleOrDefaultAsync(r => r.BookingRequestItemId == bookingRequestItemId, ct);
        if (existing is not null && existing.Status != StockReservationStatus.Pending)
        {
            return StockOperationResult.Failure(
                StockOperationOutcome.AlreadyReserved,
                $"This booking request item already has a stock reservation in status '{existing.Status}'.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        try
        {
            // The guarded statement DOCS §07 specifies verbatim. chk_never_oversold
            // (reserved_quantity <= stock_quantity) makes Postgres itself refuse the second of two
            // concurrent requests for the last units of stock — this call either fully succeeds or
            // throws, there is no partial state to unwind by hand.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE consumables SET reserved_quantity = reserved_quantity + {item.Quantity}, updated_at = now() WHERE id = {item.ConsumableId}", ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return StockOperationResult.Failure(
                StockOperationOutcome.InsufficientStock,
                $"Not enough available stock to reserve {item.Quantity} unit(s) of this consumable.");
        }

        var consumable = await db.Consumables.AsNoTracking().SingleAsync(c => c.Id == item.ConsumableId, ct);

        StockReservation reservation;
        if (existing is not null)
        {
            existing.Status = StockReservationStatus.Reserved;
            existing.ReservedAt = DateTimeOffset.UtcNow;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            reservation = existing;
        }
        else
        {
            reservation = new StockReservation
            {
                Id = Guid.NewGuid(),
                BookingRequestItemId = bookingRequestItemId,
                ConsumableId = item.ConsumableId,
                Quantity = item.Quantity,
                Status = StockReservationStatus.Reserved,
                ReservedAt = DateTimeOffset.UtcNow,
            };
            db.StockReservations.Add(reservation);
        }

        db.StockTransactions.Add(new StockTransaction
        {
            Id = Guid.NewGuid(),
            ConsumableId = item.ConsumableId,
            TransactionType = StockTransactionType.Reserve,
            Quantity = -item.Quantity, // reduces what is still available, on-hand stock is untouched
            BalanceAfter = consumable.StockQuantity,
            BookingRequestId = item.BookingRequestId,
            StockReservationId = reservation.Id,
            CreatedBy = performedByUserId,
        });
        await db.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);

        var withConsumable = await db.StockReservations.AsNoTracking().Include(r => r.Consumable)
            .SingleAsync(r => r.Id == reservation.Id, ct);
        return StockOperationResult.Success(withConsumable);
    }

    public async Task<StockOperationResult> ReleaseAsync(Guid reservationId, Guid performedByUserId, CancellationToken ct)
    {
        var reservation = await db.StockReservations.SingleOrDefaultAsync(r => r.Id == reservationId, ct);
        if (reservation is null)
        {
            return StockOperationResult.Failure(StockOperationOutcome.ReservationNotFound, "Stock reservation not found.");
        }

        // DOCS S3 test list: "can only release Reserved items" — a Pending record never held stock,
        // and a Released/Used one has nothing left to give back.
        if (reservation.Status != StockReservationStatus.Reserved)
        {
            return StockOperationResult.Failure(
                StockOperationOutcome.InvalidState,
                $"Only a 'Reserved' reservation can be released; this one is '{reservation.Status}'.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE consumables SET reserved_quantity = reserved_quantity - {reservation.Quantity}, updated_at = now() WHERE id = {reservation.ConsumableId}", ct);

        var consumable = await db.Consumables.AsNoTracking().SingleAsync(c => c.Id == reservation.ConsumableId, ct);

        reservation.Status = StockReservationStatus.Released;
        reservation.ReleasedAt = DateTimeOffset.UtcNow;
        reservation.UpdatedAt = DateTimeOffset.UtcNow;

        db.StockTransactions.Add(new StockTransaction
        {
            Id = Guid.NewGuid(),
            ConsumableId = reservation.ConsumableId,
            TransactionType = StockTransactionType.Release,
            Quantity = reservation.Quantity, // gives the units back to available stock
            BalanceAfter = consumable.StockQuantity,
            StockReservationId = reservation.Id,
            CreatedBy = performedByUserId,
        });
        await db.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);
        reservation.Consumable = consumable; // populate for the response mapper only, set post-save so it can't affect the write
        return StockOperationResult.Success(reservation, consumable);
    }

    public async Task<StockOperationResult> MarkUsedAsync(Guid reservationId, Guid performedByUserId, CancellationToken ct)
    {
        var reservation = await db.StockReservations.SingleOrDefaultAsync(r => r.Id == reservationId, ct);
        if (reservation is null)
        {
            return StockOperationResult.Failure(StockOperationOutcome.ReservationNotFound, "Stock reservation not found.");
        }

        if (reservation.Status != StockReservationStatus.Reserved)
        {
            return StockOperationResult.Failure(
                StockOperationOutcome.InvalidState,
                $"Only a 'Reserved' reservation can be marked used; this one is '{reservation.Status}'.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // The consumable is physically gone: both counters move together, so
        // chk_never_oversold stays satisfied and reserved never outlives the stock it held.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE consumables
             SET stock_quantity = stock_quantity - {reservation.Quantity},
                 reserved_quantity = reserved_quantity - {reservation.Quantity},
                 updated_at = now()
             WHERE id = {reservation.ConsumableId}
             """, ct);

        var consumable = await db.Consumables.AsNoTracking().SingleAsync(c => c.Id == reservation.ConsumableId, ct);

        reservation.Status = StockReservationStatus.Used;
        reservation.UsedAt = DateTimeOffset.UtcNow;
        reservation.UpdatedAt = DateTimeOffset.UtcNow;

        db.StockTransactions.Add(new StockTransaction
        {
            Id = Guid.NewGuid(),
            ConsumableId = reservation.ConsumableId,
            TransactionType = StockTransactionType.StockOut,
            Quantity = -reservation.Quantity,
            BalanceAfter = consumable.StockQuantity,
            StockReservationId = reservation.Id,
            CreatedBy = performedByUserId,
        });
        await db.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);
        reservation.Consumable = consumable; // populate for the response mapper only, set post-save so it can't affect the write
        return StockOperationResult.Success(reservation, consumable);
    }
}
