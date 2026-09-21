using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StudyHive.Api.Common;
using StudyHive.Api.Data;
using StudyHive.Api.Data.Entities;

namespace StudyHive.Api.Controllers.Store;

/// <summary>S3: suppliers and which consumables they supply. See DOCS §11 API table.</summary>
[ApiController]
[Route("api/suppliers")]
[Authorize]
public sealed class SuppliersController(StudyHiveDbContext db) : ControllerBase
{
    [HttpPost]
    [Authorize(Roles = $"{Roles.StoreOfficer},{Roles.Admin}")]
    [ProducesResponseType(typeof(SupplierResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CreateSupplierRequest request, CancellationToken ct)
    {
        var nameTaken = await db.Suppliers.AsNoTracking().AnyAsync(s => s.Name == request.Name.Trim(), ct);
        if (nameTaken)
        {
            return Problem(
                type: "https://studyhive.dev/errors/conflict",
                title: "Supplier name already exists",
                statusCode: StatusCodes.Status409Conflict,
                detail: $"A supplier named '{request.Name}' already exists.");
        }

        var supplier = new Supplier
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            ContactEmail = request.ContactEmail.Trim(),
            Phone = request.Phone.Trim(),
            Address = request.Address?.Trim(),
        };

        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(List), null, SupplierResponse.From(supplier));
    }

    /// <summary>Backs W-23.</summary>
    [HttpGet]
    [Authorize(Roles = Roles.StoreOfficer)]
    [ProducesResponseType(typeof(PagedResult<SupplierResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] PageQuery query, [FromQuery] bool activeOnly = true, CancellationToken ct = default)
    {
        IQueryable<Supplier> suppliers = db.Suppliers.AsNoTracking();

        if (activeOnly)
        {
            suppliers = suppliers.Where(s => s.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = $"%{query.Search.Trim()}%";
            suppliers = suppliers.Where(s => EF.Functions.ILike(s.Name, search));
        }

        var sortDescending = string.Equals(query.SortDir, "desc", StringComparison.OrdinalIgnoreCase);
        IOrderedQueryable<Supplier>? sorted = query.SortBy?.ToLowerInvariant() switch
        {
            null or "" or "name" => sortDescending ? suppliers.OrderByDescending(s => s.Name) : suppliers.OrderBy(s => s.Name),
            "createdat" => sortDescending ? suppliers.OrderByDescending(s => s.CreatedAt) : suppliers.OrderBy(s => s.CreatedAt),
            _ => null,
        };
        if (sorted is null)
        {
            ModelState.AddModelError(nameof(query.SortBy), $"Unknown sortBy value '{query.SortBy}'.");
            return ValidationProblem(ModelState);
        }
        suppliers = sorted;

        var totalItems = await suppliers.CountAsync(ct);
        var items = await suppliers
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(s => SupplierResponse.From(s))
            .ToListAsync(ct);

        return Ok(PagedResult<SupplierResponse>.Create(items, query.Page, query.PageSize, totalItems));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = Roles.StoreOfficer)]
    [ProducesResponseType(typeof(SupplierResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid id, UpdateSupplierRequest request, CancellationToken ct)
    {
        var supplier = await db.Suppliers.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (supplier is null) return NotFound();

        var nameTaken = await db.Suppliers.AsNoTracking().AnyAsync(s => s.Id != id && s.Name == request.Name.Trim(), ct);
        if (nameTaken)
        {
            return Problem(
                type: "https://studyhive.dev/errors/conflict",
                title: "Supplier name already exists",
                statusCode: StatusCodes.Status409Conflict,
                detail: $"A supplier named '{request.Name}' already exists.");
        }

        supplier.Name = request.Name.Trim();
        supplier.ContactEmail = request.ContactEmail.Trim();
        supplier.Phone = request.Phone.Trim();
        supplier.Address = request.Address?.Trim();
        supplier.IsActive = request.IsActive;
        supplier.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Ok(SupplierResponse.From(supplier));
    }
}
