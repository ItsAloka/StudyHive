using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StudyHive.Api.Common;
using StudyHive.Api.Contracts;
using StudyHive.Api.Data;
using StudyHive.Api.Data.Entities;

namespace StudyHive.Api.Services;

/// <summary>
/// Runs one booking request's agentic workflow end to end: calls the Planner Agent, then the real
/// Resource Agent (S3 — availability/pricing, plus one Pending stock_reservations row per line so
/// the librarian's approval screen can see what would be reserved), then persists contract-shaped
/// Scheduling/Validation stub steps (DOCS §04: "Use contract-correct fake Scheduling/... Validation
/// outputs until later owners replace them") and moves the request to PendingApproval. Every failure
/// path (ineligible, planner/resource unreachable, workflow timeout) ends in a terminal
/// Failed/Rejected status with an error code — never a half-updated request.
/// </summary>
public interface IWorkflowOrchestrationService
{
    Task<Guid> StartAsync(Guid bookingRequestId, CancellationToken ct);
    Task RunAsync(Guid workflowExecutionId, CancellationToken ct);
}

public sealed class WorkflowOrchestrationService(
    StudyHiveDbContext db,
    IBookingEligibilityService eligibilityService,
    IPlannerClient plannerClient,
    ISchedulingAgentClient schedulingAgentClient,
    IResourceClient resourceClient,
    IConsumableStockService stockService,
    IOptions<WorkflowLimitsOptions> limitsOptions) : IWorkflowOrchestrationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<Guid> StartAsync(Guid bookingRequestId, CancellationToken ct)
    {
        var bookingRequest = await db.BookingRequests.SingleAsync(b => b.Id == bookingRequestId, ct);

        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            BookingRequestId = bookingRequestId,
            Objective = bookingRequest.Objective,
            Status = WorkflowStatus.Started,
            StartedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.WorkflowExecutions.Add(execution);

        bookingRequest.Status = BookingRequestStatus.Submitted;
        bookingRequest.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return execution.Id;
    }

    public async Task RunAsync(Guid workflowExecutionId, CancellationToken outerCt)
    {
        var limits = limitsOptions.Value;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(limits.WholeWorkflowTimeoutSeconds));
        var ct = timeoutCts.Token;

        var execution = await db.WorkflowExecutions
            .Include(w => w.BookingRequest).ThenInclude(b => b.Items)
            .Include(w => w.BookingRequest).ThenInclude(b => b.RequiredEquipment)
            .SingleOrDefaultAsync(w => w.Id == workflowExecutionId, ct);
        if (execution is null) return;

        var bookingRequest = execution.BookingRequest;

        execution.Status = WorkflowStatus.InProgress;
        execution.UpdatedAt = DateTimeOffset.UtcNow;
        bookingRequest.Status = BookingRequestStatus.Processing;
        bookingRequest.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        try
        {
            var eligibility = await eligibilityService.EvaluateAsync(bookingRequest.StudentId, ct);

            var plannerRequest = new PlannerRequest
            {
                Objective = bookingRequest.Objective,
                StudentId = bookingRequest.StudentId,
                GroupSize = bookingRequest.GroupSize,
                PreferredDateFrom = bookingRequest.PreferredDateFrom,
                PreferredDateTo = bookingRequest.PreferredDateTo,
                PreferredTimeFrom = bookingRequest.PreferredTimeFrom,
                PreferredTimeTo = bookingRequest.PreferredTimeTo,
                SessionsRequired = bookingRequest.SessionsRequired,
                SessionDurationMinutes = bookingRequest.SessionDurationMinutes,
                Budget = bookingRequest.Budget,
                StudentEligible = eligibility.IsEligible,
                EligibilityReasons = eligibility.Reasons,
                RequestedItems = bookingRequest.Items
                    .Select(i => new PlannerRequestItem { ConsumableId = i.ConsumableId, Quantity = i.Quantity })
                    .ToList(),
            };

            var (plannerResponse, stepDurationMs, stepError) = await CallPlannerWithRetriesAsync(plannerRequest, limits, ct);

            await LogStepAsync(
                execution.Id, stepNumber: 1, agentName: "Planner", toolName: "create_plan",
                input: plannerRequest,
                output: plannerResponse is null ? new { error = stepError } : plannerResponse,
                validationResult: plannerResponse is not null ? StepValidationResult.Pass : StepValidationResult.Fail,
                errorMessage: stepError, durationMs: stepDurationMs, ct);

            if (plannerResponse is null)
            {
                await FailAsync(execution, bookingRequest, "STEP_RETRY_EXHAUSTED", stepError ?? "Planner did not respond after retries.", ct);
                return;
            }

            execution.PlanJson = JsonSerializer.Serialize(plannerResponse, JsonOptions);

            if (!plannerResponse.Eligible)
            {
                execution.Status = WorkflowStatus.Rejected;
                execution.ErrorCode = "INELIGIBLE";
                execution.ErrorMessage = plannerResponse.Reasons.Count > 0
                    ? string.Join(" ", plannerResponse.Reasons)
                    : "Planner determined the student is not eligible.";
                execution.CompletedAt = DateTimeOffset.UtcNow;
                execution.UpdatedAt = DateTimeOffset.UtcNow;
                execution.CurrentStep = 1;
                execution.TotalSteps = 1;

                bookingRequest.Status = BookingRequestStatus.Rejected;
                bookingRequest.UpdatedAt = DateTimeOffset.UtcNow;

                await db.SaveChangesAsync(ct);
                return;
            }

            // Step 2 is S2's real Scheduling Agent. StudyHive.Api supplies the trusted room,
            // equipment, booking and maintenance snapshot; the agent never reads the database.
            var schedulingRequest = await BuildSchedulingRequestAsync(bookingRequest, ct);
            var (schedulingOutput, schedulingDurationMs, schedulingError) =
                await CallSchedulingWithRetriesAsync(schedulingRequest, limits, ct);

            var schedulingSucceeded = schedulingOutput is not null &&
                schedulingOutput.Slots.Count >= bookingRequest.SessionsRequired;
            var noAvailabilityError = schedulingOutput is not null && !schedulingSucceeded
                ? string.Join(" ", schedulingOutput.Conflicts)
                : null;

            await LogStepAsync(execution.Id, 2, "Scheduling", "propose_slots",
                input: schedulingRequest,
                output: schedulingOutput is null ? new { error = schedulingError } : schedulingOutput,
                validationResult: schedulingSucceeded ? StepValidationResult.Pass : StepValidationResult.Fail,
                errorMessage: schedulingError ?? noAvailabilityError,
                durationMs: schedulingDurationMs,
                ct);

            if (schedulingOutput is null)
            {
                await FailAsync(execution, bookingRequest, "STEP_RETRY_EXHAUSTED",
                    schedulingError ?? "Scheduling Agent did not respond after retries.", ct);
                return;
            }

            if (!schedulingSucceeded)
            {
                await FailAsync(execution, bookingRequest, "NO_ROOM_AVAILABLE",
                    string.IsNullOrWhiteSpace(noAvailabilityError)
                        ? "The Scheduling Agent could not find all requested sessions."
                        : noAvailabilityError,
                    ct);
                return;
            }

            // Steps 3-4 remain the S3/S4 contract-shaped stubs until their owners replace them.
            // Step 3: the real Resource Agent (S3). The agent has no database access, so — exactly
            // how `plannerRequest` above carries eligibility already computed — this API reads each
            // requested consumable's current availability/price itself and hands both down on the
            // wire. A consumable that no longer exists (or was deactivated) falls back to
            // Available=0, which correctly makes that line `sufficient: false` rather than throwing.
            var consumableIds = bookingRequest.Items.Select(i => i.ConsumableId).Distinct().ToList();
            var consumablesById = await db.Consumables.AsNoTracking()
                .Where(c => consumableIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, ct);

            var resourceRequest = new ResourceRequest
            {
                RequestedItems = bookingRequest.Items.Select(i =>
                {
                    consumablesById.TryGetValue(i.ConsumableId, out var consumable);
                    return new ResourceRequestItem
                    {
                        ConsumableId = i.ConsumableId,
                        Name = consumable?.Name ?? "Unknown consumable",
                        Requested = i.Quantity,
                        Available = consumable?.AvailableQuantity ?? 0,
                        UnitPrice = consumable?.UnitPrice ?? 0m,
                    };
                }).ToList(),
            };

            var (resourceResponse, resourceDurationMs, resourceError) = await CallResourceWithRetriesAsync(resourceRequest, limits, ct);

            await LogStepAsync(
                execution.Id, stepNumber: 3, agentName: "Resource", toolName: "prepare_reservation",
                input: resourceRequest,
                output: resourceResponse is null ? new { error = resourceError } : resourceResponse,
                validationResult: resourceResponse is not null ? StepValidationResult.Pass : StepValidationResult.Fail,
                errorMessage: resourceError, durationMs: resourceDurationMs, ct);

            if (resourceResponse is null)
            {
                await FailAsync(execution, bookingRequest, "STEP_RETRY_EXHAUSTED", resourceError ?? "Resource agent did not respond after retries.", ct);
                return;
            }

            // DOCS §11: the Resource agent "creates Pending reservation records but does not
            // actually reserve stock" — one per booking-request line, regardless of sufficiency, so
            // the librarian's approval screen shows exactly what would be reserved (and what's
            // short) before they decide. Real reservation (the guarded, no-oversell transition to
            // Reserved) happens later, at approval time, via IConsumableStockService.ReserveAsync.
            foreach (var item in bookingRequest.Items)
            {
                var pendingResult = await stockService.CreatePendingReservationAsync(item.Id, ct);
                if (!pendingResult.Succeeded && pendingResult.Outcome != StockOperationOutcome.AlreadyReserved)
                {
                    await FailAsync(execution, bookingRequest, "RESOURCE_PENDING_RESERVATION_FAILED",
                        pendingResult.Detail ?? "Could not create a pending stock reservation.", ct);
                    return;
                }
            }

            // Step 4: contract-shaped Validation stub — S4 replaces this in its own relay handoff.
            var validationOutput = BuildValidationStub(bookingRequest);
            await LogStepAsync(execution.Id, 4, "Validation", "calculate_quotation",
                input: new { }, output: validationOutput, StepValidationResult.Pass, null, durationMs: 0, ct);

            execution.Status = WorkflowStatus.PendingApproval;
            execution.CurrentStep = 4;
            execution.TotalSteps = 4;
            execution.UpdatedAt = DateTimeOffset.UtcNow;

            bookingRequest.Status = BookingRequestStatus.PendingApproval;
            bookingRequest.UpdatedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (!outerCt.IsCancellationRequested)
        {
            await FailAsync(execution, bookingRequest, "WORKFLOW_TIMEOUT",
                $"Workflow exceeded the {limits.WholeWorkflowTimeoutSeconds}s limit.", CancellationToken.None);
        }
    }

    private async Task<(PlannerResponse? Response, int DurationMs, string? Error)> CallPlannerWithRetriesAsync(
        PlannerRequest request, WorkflowLimitsOptions limits, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        string? lastError = null;
        var maxAttempts = limits.MaxRetriesPerStep + 1;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(TimeSpan.FromSeconds(limits.ToolCallTimeoutSeconds));
            try
            {
                var response = await plannerClient.PlanAsync(request, attemptCts.Token);
                stopwatch.Stop();
                return (response, (int)stopwatch.ElapsedMilliseconds, null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = $"Planner call timed out after {limits.ToolCallTimeoutSeconds}s (attempt {attempt}/{maxAttempts}).";
            }
            catch (HttpRequestException ex)
            {
                lastError = $"Planner call failed: {ex.Message} (attempt {attempt}/{maxAttempts}).";
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                lastError = $"Planner returned an invalid response: {ex.Message} (attempt {attempt}/{maxAttempts}).";
            }
        }

        stopwatch.Stop();
        return (null, (int)stopwatch.ElapsedMilliseconds, lastError);
    }

    private async Task<SchedulingRequest> BuildSchedulingRequestAsync(
        BookingRequest bookingRequest,
        CancellationToken ct)
    {
        var colomboOffset = TimeSpan.FromMinutes(330);
        var rangeStart = new DateTimeOffset(
            bookingRequest.PreferredDateFrom.ToDateTime(TimeOnly.MinValue),
            colomboOffset).ToUniversalTime();
        var rangeEnd = new DateTimeOffset(
            bookingRequest.PreferredDateTo.ToDateTime(TimeOnly.MaxValue),
            colomboOffset).ToUniversalTime();

        var rooms = await db.StudyRooms
            .AsNoTracking()
            .AsSplitQuery()
            .Include(r => r.Equipment.Where(e => e.Quantity > 0))
            .Include(r => r.Bookings.Where(b =>
                b.Status == RoomBookingStatus.Confirmed &&
                b.StartsAt < rangeEnd &&
                b.EndsAt > rangeStart))
            .Include(r => r.MaintenanceWindows.Where(w =>
                w.StartsAt < rangeEnd &&
                w.EndsAt > rangeStart))
            .ToListAsync(ct);

        return new SchedulingRequest
        {
            GroupSize = bookingRequest.GroupSize,
            PreferredDateFrom = bookingRequest.PreferredDateFrom,
            PreferredDateTo = bookingRequest.PreferredDateTo,
            PreferredTimeFrom = bookingRequest.PreferredTimeFrom,
            PreferredTimeTo = bookingRequest.PreferredTimeTo,
            SessionsRequired = bookingRequest.SessionsRequired,
            SessionDurationMinutes = bookingRequest.SessionDurationMinutes,
            RequiredEquipmentTypeIds = bookingRequest.RequiredEquipment
                .Select(e => e.EquipmentTypeId)
                .Distinct()
                .ToList(),
            Rooms = rooms.Select(room => new SchedulingRoom
            {
                RoomId = room.Id,
                RoomName = room.Name,
                Capacity = room.Capacity,
                HourlyRate = room.HourlyRate,
                IsActive = room.IsActive,
                EquipmentTypeIds = room.Equipment
                    .Select(e => e.EquipmentTypeId)
                    .Distinct()
                    .ToList(),
                Bookings = room.Bookings.Select(b => new SchedulingTimeBlock
                {
                    StartsAt = b.StartsAt,
                    EndsAt = b.EndsAt,
                }).ToList(),
                MaintenanceWindows = room.MaintenanceWindows.Select(w => new SchedulingTimeBlock
                {
                    StartsAt = w.StartsAt,
                    EndsAt = w.EndsAt,
                }).ToList(),
            }).ToList(),
        };
    }

    private async Task<(SchedulingResponse? Response, int DurationMs, string? Error)>
        CallSchedulingWithRetriesAsync(
            SchedulingRequest request,
            WorkflowLimitsOptions limits,
            CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        string? lastError = null;
        var maxAttempts = limits.MaxRetriesPerStep + 1;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(TimeSpan.FromSeconds(limits.ToolCallTimeoutSeconds));
            try
            {
                var response = await schedulingAgentClient.ProposeAsync(request, attemptCts.Token);
                var validationErrors = ValidateSchedulingResponse(request, response);
                if (validationErrors.Count == 0)
                {
                    stopwatch.Stop();
                    return (response, (int)stopwatch.ElapsedMilliseconds, null);
                }

                lastError = $"Scheduling validation failed: {string.Join(" ", validationErrors)} " +
                    $"(attempt {attempt}/{maxAttempts}).";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = $"Scheduling call timed out after {limits.ToolCallTimeoutSeconds}s " +
                    $"(attempt {attempt}/{maxAttempts}).";
            }
            catch (HttpRequestException ex)
            {
                lastError = $"Scheduling call failed: {ex.Message} (attempt {attempt}/{maxAttempts}).";
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                lastError = $"Scheduling Agent returned an invalid response: {ex.Message} " +
                    $"(attempt {attempt}/{maxAttempts}).";
            }
        }

        stopwatch.Stop();
        return (null, (int)stopwatch.ElapsedMilliseconds, lastError);
    }

    private static IReadOnlyList<string> ValidateSchedulingResponse(
        SchedulingRequest request,
        SchedulingResponse response)
    {
        var errors = new List<string>();
        var requiredEquipment = request.RequiredEquipmentTypeIds.ToHashSet();
        var duration = TimeSpan.FromMinutes(request.SessionDurationMinutes);
        var colomboOffset = TimeSpan.FromMinutes(330);

        foreach (var slot in response.Slots)
        {
            var room = request.Rooms.SingleOrDefault(r => r.RoomId == slot.RoomId);
            if (room is null)
            {
                errors.Add($"Unknown room {slot.RoomId}.");
                continue;
            }

            if (!room.IsActive || room.Capacity < request.GroupSize ||
                !requiredEquipment.IsSubsetOf(room.EquipmentTypeIds.ToHashSet()))
            {
                errors.Add($"Room {slot.RoomId} does not satisfy the request.");
            }

            if (slot.EndsAt - slot.StartsAt != duration)
            {
                errors.Add($"Room {slot.RoomId} has an invalid slot duration.");
            }

            var localStart = slot.StartsAt.ToOffset(colomboOffset);
            var localEnd = slot.EndsAt.ToOffset(colomboOffset);
            if (DateOnly.FromDateTime(localStart.DateTime) < request.PreferredDateFrom ||
                DateOnly.FromDateTime(localEnd.DateTime) > request.PreferredDateTo ||
                TimeOnly.FromDateTime(localStart.DateTime) < request.PreferredTimeFrom ||
                TimeOnly.FromDateTime(localEnd.DateTime) > request.PreferredTimeTo)
            {
                errors.Add($"Room {slot.RoomId} has a slot outside the preferred window.");
            }

            if (room.Bookings.Any(b => b.StartsAt < slot.EndsAt && b.EndsAt > slot.StartsAt) ||
                room.MaintenanceWindows.Any(w => w.StartsAt < slot.EndsAt && w.EndsAt > slot.StartsAt))
            {
                errors.Add($"Room {slot.RoomId} has a conflicting slot.");
            }
        }

        for (var i = 0; i < response.Slots.Count; i++)
        {
            for (var j = i + 1; j < response.Slots.Count; j++)
            {
                if (response.Slots[i].StartsAt < response.Slots[j].EndsAt &&
                    response.Slots[i].EndsAt > response.Slots[j].StartsAt)
                {
                    errors.Add("Proposed sessions overlap each other.");
                }
            }
        }

        return errors;
    }

    private async Task<(ResourceResponse? Response, int DurationMs, string? Error)> CallResourceWithRetriesAsync(
        ResourceRequest request, WorkflowLimitsOptions limits, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        string? lastError = null;
        var maxAttempts = limits.MaxRetriesPerStep + 1;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(TimeSpan.FromSeconds(limits.ToolCallTimeoutSeconds));
            try
            {
                var response = await resourceClient.PrepareReservationAsync(request, attemptCts.Token);
                stopwatch.Stop();
                return (response, (int)stopwatch.ElapsedMilliseconds, null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = $"Resource call timed out after {limits.ToolCallTimeoutSeconds}s (attempt {attempt}/{maxAttempts}).";
            }
            catch (HttpRequestException ex)
            {
                lastError = $"Resource call failed: {ex.Message} (attempt {attempt}/{maxAttempts}).";
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                lastError = $"Resource returned an invalid response: {ex.Message} (attempt {attempt}/{maxAttempts}).";
            }
        }

        stopwatch.Stop();
        return (null, (int)stopwatch.ElapsedMilliseconds, lastError);
    }

    private async Task FailAsync(WorkflowExecution execution, BookingRequest bookingRequest, string errorCode, string errorMessage, CancellationToken ct)
    {
        execution.Status = WorkflowStatus.Failed;
        execution.ErrorCode = errorCode;
        execution.ErrorMessage = errorMessage;
        execution.CompletedAt = DateTimeOffset.UtcNow;
        execution.UpdatedAt = DateTimeOffset.UtcNow;

        bookingRequest.Status = BookingRequestStatus.Failed;
        bookingRequest.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    private async Task LogStepAsync(
        Guid workflowExecutionId, int stepNumber, string agentName, string toolName,
        object? input, object? output, StepValidationResult validationResult, string? errorMessage, int durationMs,
        CancellationToken ct)
    {
        db.WorkflowStepLogs.Add(new WorkflowStepLog
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = workflowExecutionId,
            StepNumber = stepNumber,
            AgentName = agentName,
            ToolName = toolName,
            InputJson = input is null ? null : JsonSerializer.Serialize(input, JsonOptions),
            OutputJson = output is null ? null : JsonSerializer.Serialize(output, JsonOptions),
            ValidationResult = validationResult,
            ErrorMessage = errorMessage,
            DurationMs = durationMs,
        });
        await db.SaveChangesAsync(ct);
    }

    private static object BuildValidationStub(BookingRequest br)
    {
        // Deliberately naive placeholder arithmetic — S4 (Costing & Approval) replaces this with the
        // real deterministic Validation agent. It exists only so PendingApproval carries a plausible,
        // contract-shaped quotation for a librarian to look at.
        var hours = br.SessionDurationMinutes / 60m;
        var roomFee = hours * 10m * br.SessionsRequired;
        var consumableCost = br.Items.Sum(i => i.Quantity * 1m);
        var total = roomFee + consumableCost;

        return new
        {
            stub = true,
            valid = true,
            results = new[]
            {
                new { rule = "validate_capacity", passed = true, detail = "Stub: capacity check deferred to S2." },
                new { rule = "validate_stock", passed = true, detail = "Stub: this stage's own re-check deferred to S4 (step 3's Resource output already has the real availability)." },
                new { rule = "validate_budget", passed = total <= br.Budget, detail = $"Stub estimate {total:0.00} vs budget {br.Budget:0.00}." },
            },
            quotation = new
            {
                roomFee,
                consumableCost,
                total,
                lineItems = Array.Empty<object>(),
            },
            failures = Array.Empty<object>(),
        };
    }
}
