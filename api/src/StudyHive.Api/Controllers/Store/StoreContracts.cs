using System.ComponentModel.DataAnnotations;
using StudyHive.Api.Data.Entities;

namespace StudyHive.Api.Controllers.Store;

// ---------------------------------------------------------------------------------------------
// Consumables
// ---------------------------------------------------------------------------------------------

public sealed class CreateConsumableRequest
{
    [Required, MaxLength(120)]
    public required string Name { get; init; }

    [MaxLength(2000)]
    public string? Description { get; init; }

    [Required, MaxLength(20)]
    public required string Unit { get; init; }

    // numeric(12,2) in Postgres (S3Configurations.cs) — 10 integer digits max.
    [Range(typeof(decimal), "0", "9999999999.99")]
    public decimal UnitPrice { get; init; }

    [Range(0, int.MaxValue)]
    public int StockQuantity { get; init; }

    [Range(0, int.MaxValue)]
    public int MinStockLevel { get; init; }
}

public sealed class UpdateConsumableRequest
{
    [Required, MaxLength(120)]
    public required string Name { get; init; }

    [MaxLength(2000)]
    public string? Description { get; init; }

    [Required, MaxLength(20)]
    public required string Unit { get; init; }

    [Range(typeof(decimal), "0", "9999999999.99")]
    public decimal UnitPrice { get; init; }

    [Range(0, int.MaxValue)]
    public int MinStockLevel { get; init; }
}

/// <summary>Stock-in is a business operation, not an edit — it only ever adds. Use <see cref="StockAdjustmentRequest"/>
/// on the ledger for corrections that can go either way.</summary>
public sealed class StockInRequest
{
    [Range(1, int.MaxValue)]
    public int Quantity { get; init; }

    [MaxLength(500)]
    public string? Notes { get; init; }
}

public sealed class ConsumableResponse
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string? Description { get; init; }
    public required string Unit { get; init; }
    public required decimal UnitPrice { get; init; }
    public required int StockQuantity { get; init; }
    public required int ReservedQuantity { get; init; }
    public required int AvailableQuantity { get; init; }
    public required int MinStockLevel { get; init; }
    public required bool IsLowStock { get; init; }
    public required bool IsActive { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    public static ConsumableResponse From(Consumable c) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Description = c.Description,
        Unit = c.Unit,
        UnitPrice = c.UnitPrice,
        StockQuantity = c.StockQuantity,
        ReservedQuantity = c.ReservedQuantity,
        AvailableQuantity = c.AvailableQuantity,
        MinStockLevel = c.MinStockLevel,
        IsLowStock = c.StockQuantity <= c.MinStockLevel,
        IsActive = c.IsActive,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt,
    };
}

/// <summary>W-20: consumable detail plus its recent ledger, in one response so the page needs one call.</summary>
public sealed class ConsumableDetailResponse
{
    public required ConsumableResponse Consumable { get; init; }
    public required IReadOnlyList<StockTransactionResponse> RecentTransactions { get; init; }
}

// ---------------------------------------------------------------------------------------------
// Suppliers
// ---------------------------------------------------------------------------------------------

public sealed class CreateSupplierRequest
{
    [Required, MaxLength(120)]
    public required string Name { get; init; }

    [Required, EmailAddress, MaxLength(320)]
    public required string ContactEmail { get; init; }

    [Required, MaxLength(30)]
    public required string Phone { get; init; }

    [MaxLength(2000)]
    public string? Address { get; init; }
}

public sealed class UpdateSupplierRequest
{
    [Required, MaxLength(120)]
    public required string Name { get; init; }

    [Required, EmailAddress, MaxLength(320)]
    public required string ContactEmail { get; init; }

    [Required, MaxLength(30)]
    public required string Phone { get; init; }

    [MaxLength(2000)]
    public string? Address { get; init; }

    public bool IsActive { get; init; } = true;
}

public sealed class SupplierResponse
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string ContactEmail { get; init; }
    public required string Phone { get; init; }
    public required string? Address { get; init; }
    public required bool IsActive { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    public static SupplierResponse From(Supplier s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        ContactEmail = s.ContactEmail,
        Phone = s.Phone,
        Address = s.Address,
        IsActive = s.IsActive,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
    };
}

// ---------------------------------------------------------------------------------------------
// Stock reservations
// ---------------------------------------------------------------------------------------------

/// <summary>One booking-request line is the single source of truth for which consumable and how
/// much — the reservation never carries its own copy of either, so the two can't drift apart.</summary>
public sealed class CreateStockReservationRequest
{
    public required Guid BookingRequestItemId { get; init; }
}

public sealed class StockReservationResponse
{
    public required Guid Id { get; init; }
    public required Guid BookingRequestItemId { get; init; }
    public required Guid ConsumableId { get; init; }
    public required string ConsumableName { get; init; }
    public required int Quantity { get; init; }
    public required StockReservationStatus Status { get; init; }
    public required DateTimeOffset? ReservedAt { get; init; }
    public required DateTimeOffset? ReleasedAt { get; init; }
    public required DateTimeOffset? UsedAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public static StockReservationResponse From(StockReservation r) => new()
    {
        Id = r.Id,
        BookingRequestItemId = r.BookingRequestItemId,
        ConsumableId = r.ConsumableId,
        ConsumableName = r.Consumable.Name,
        Quantity = r.Quantity,
        Status = r.Status,
        ReservedAt = r.ReservedAt,
        ReleasedAt = r.ReleasedAt,
        UsedAt = r.UsedAt,
        CreatedAt = r.CreatedAt,
    };
}

// ---------------------------------------------------------------------------------------------
// Stock transactions (append-only ledger)
// ---------------------------------------------------------------------------------------------

public sealed class StockTransactionResponse
{
    public required Guid Id { get; init; }
    public required Guid ConsumableId { get; init; }
    public required StockTransactionType TransactionType { get; init; }
    public required int Quantity { get; init; }
    public required int BalanceAfter { get; init; }
    public required Guid? BookingRequestId { get; init; }
    public required Guid? StockReservationId { get; init; }
    public required string? Notes { get; init; }
    public required Guid CreatedBy { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public static StockTransactionResponse From(StockTransaction t) => new()
    {
        Id = t.Id,
        ConsumableId = t.ConsumableId,
        TransactionType = t.TransactionType,
        Quantity = t.Quantity,
        BalanceAfter = t.BalanceAfter,
        BookingRequestId = t.BookingRequestId,
        StockReservationId = t.StockReservationId,
        Notes = t.Notes,
        CreatedBy = t.CreatedBy,
        CreatedAt = t.CreatedAt,
    };
}
