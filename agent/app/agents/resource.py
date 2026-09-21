"""Resource Agent (S3) — DOCS §11: "Checks consumable availability and prices. Creates Pending
reservation records but does not actually reserve stock — real reservation happens only after
librarian approval via a database transaction."

Deterministic by design, for the same reason app/agents/planner.py is: the allow-listed tools below
are plain arithmetic over data the caller already supplied, never a model call, and never a database
query. This agent has no database access (DOCS §11: "never calls the DB directly") — StudyHive.Api
looks up each consumable's current `available`/`unit_price` itself (ConsumablesController /
ConsumableStockService already own that read) and hands both to this service on the wire, the exact
same shape PlannerRequest already carries `student_eligible` rather than asking the Planner to derive
it. `check_stock` therefore has nothing to check except the two numbers it was given — there is no
"real" stock check happening anywhere in this file, only in the `available` figure the caller
supplied and in the `chk_never_oversold` CHECK constraint that guards the real reservation later.

That also means this agent decides nothing StudyHive.Api couldn't already see: `sufficient` and
`allAvailable` are exactly `available >= requested`, computed from data the caller already had. Its
value is centralizing that arithmetic in one tested place (and, per DOCS §04, giving S3 its own real
Resource stage instead of WorkflowOrchestrationService's contract-shaped stub) — not adding any
authority a hostile caller could exploit.
"""

from __future__ import annotations

from app.schemas import ResourceRequest, ResourceRequestItem, ResourceResponse, ResourceResponseItem

# Money is rounded to cents at the one point it's computed, not left to accumulate floating-point
# noise across `requested_items` and get re-rounded differently by every caller downstream.
_MONEY_DECIMALS = 2


def check_stock(item: ResourceRequestItem) -> bool:
    """Tool: is there enough available stock for this one line?

    Trusts only the `available` figure already on the request — see the module docstring for why
    there is nothing else this agent could check.
    """
    return item.available >= item.requested


def get_prices(item: ResourceRequestItem) -> float:
    """Tool: current unit price for this line.

    A pass-through, not a lookup — `unit_price` is StudyHive.Api's own read of `consumables` at
    call time. Kept as its own named tool (rather than inlined) because DOCS §11 lists
    `get_prices` as its own allow-listed tool, and because a future revision that adds e.g.
    bulk-quantity pricing tiers has exactly one place to change.
    """
    return item.unit_price


def prepare_reservation(request: ResourceRequest) -> ResourceResponse:
    """Tool: build the per-line availability/pricing breakdown and the overall totals.

    Runs `check_stock` and `get_prices` for every requested line, in the order they arrived. An
    empty `requested_items` list is not an error (a booking request with no consumables is
    ordinary) and returns `allAvailable=True, totalCost=0` — vacuously true, matching
    WorkflowOrchestrationService's existing stub behaviour for the same case.
    """
    items = [
        ResourceResponseItem(
            consumable_id=item.consumable_id,
            name=item.name,
            requested=item.requested,
            available=item.available,
            sufficient=check_stock(item),
            unit_price=get_prices(item),
            line_total=round(get_prices(item) * item.requested, _MONEY_DECIMALS),
        )
        for item in request.requested_items
    ]

    return ResourceResponse(
        items=items,
        total_cost=round(sum(item.line_total for item in items), _MONEY_DECIMALS),
        all_available=all(item.sufficient for item in items),
    )
