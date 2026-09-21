"""Wire contracts shared with StudyHive.Api (see api/src/StudyHive.Api/Contracts/PlannerContracts.cs).

JSON over the wire is camelCase everywhere in this project (DOCS §12 shared conventions) — Python
field names stay snake_case (PEP 8) and carry a camelCase `alias` so pydantic validates incoming
requests against the C# client's actual JSON shape and FastAPI serializes responses back the same way.
"""

from __future__ import annotations

from datetime import date, datetime, time
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field


class PlannerRequestItem(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    consumable_id: UUID = Field(alias="consumableId")
    quantity: int


class PlannerRequest(BaseModel):
    """Input contract (DOCS §11 agent-io table). `student_eligible`/`eligibility_reasons` are the
    server's own verdict — see app/agents/planner.py for why this agent never recomputes them."""

    model_config = ConfigDict(populate_by_name=True)

    objective: str
    student_id: UUID = Field(alias="studentId")
    group_size: int = Field(alias="groupSize")
    preferred_date_from: date = Field(alias="preferredDateFrom")
    preferred_date_to: date = Field(alias="preferredDateTo")
    preferred_time_from: time = Field(alias="preferredTimeFrom")
    preferred_time_to: time = Field(alias="preferredTimeTo")
    sessions_required: int = Field(alias="sessionsRequired")
    session_duration_minutes: int = Field(alias="sessionDurationMinutes")
    budget: float
    student_eligible: bool = Field(alias="studentEligible")
    eligibility_reasons: list[str] = Field(default_factory=list, alias="eligibilityReasons")
    requested_items: list[PlannerRequestItem] = Field(default_factory=list, alias="requestedItems")


class PlanStep(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    n: int
    agent: str
    action: str
    params: dict[str, object] = Field(default_factory=dict)


class PlannerResponse(BaseModel):
    """Output contract (DOCS §11 agent-io table): `{ planId, eligible, reasons[], steps[] }`."""

    model_config = ConfigDict(populate_by_name=True)

    plan_id: UUID = Field(alias="planId")
    eligible: bool
    reasons: list[str] = Field(default_factory=list)
    steps: list[PlanStep] = Field(default_factory=list)


class ResourceRequestItem(BaseModel):
    """One requested line. `available`/`unit_price` are StudyHive.Api's own read of `consumables`
    at call time — this agent has no database access (DOCS §11), the same reason PlannerRequest
    carries `student_eligible` already computed rather than asking this service to derive it."""

    model_config = ConfigDict(populate_by_name=True)

    consumable_id: UUID = Field(alias="consumableId")
    name: str
    requested: int = Field(gt=0)
    available: int = Field(ge=0)
    unit_price: float = Field(alias="unitPrice", ge=0)


class ResourceRequest(BaseModel):
    """Input contract (DOCS §11 agent-io table): `{ requestedItems: [...] }`."""

    model_config = ConfigDict(populate_by_name=True)

    requested_items: list[ResourceRequestItem] = Field(default_factory=list, alias="requestedItems")


class ResourceResponseItem(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    consumable_id: UUID = Field(alias="consumableId")
    name: str
    requested: int
    available: int
    sufficient: bool
    unit_price: float = Field(alias="unitPrice")
    line_total: float = Field(alias="lineTotal")


class ResourceResponse(BaseModel):
    """Output contract (DOCS §11 agent-io table):
    `{ items[{consumableId, name, requested, available, sufficient, unitPrice, lineTotal}], totalCost, allAvailable }`."""

    model_config = ConfigDict(populate_by_name=True)

    items: list[ResourceResponseItem] = Field(default_factory=list)
    total_cost: float = Field(alias="totalCost")
    all_available: bool = Field(alias="allAvailable")
class SchedulingTimeBlock(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    starts_at: datetime = Field(alias="startsAt")
    ends_at: datetime = Field(alias="endsAt")


class SchedulingRoom(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    room_id: UUID = Field(alias="roomId")
    room_name: str = Field(alias="roomName")
    capacity: int
    hourly_rate: float = Field(alias="hourlyRate")
    is_active: bool = Field(alias="isActive")
    equipment_type_ids: list[UUID] = Field(default_factory=list, alias="equipmentTypeIds")
    bookings: list[SchedulingTimeBlock] = Field(default_factory=list)
    maintenance_windows: list[SchedulingTimeBlock] = Field(default_factory=list, alias="maintenanceWindows")


class SchedulingRequest(BaseModel):
    """S2 Scheduling Agent input. All room and conflict data comes from StudyHive.Api;
    the agent has no direct database or client access."""

    model_config = ConfigDict(populate_by_name=True)

    group_size: int = Field(alias="groupSize", ge=1)
    preferred_date_from: date = Field(alias="preferredDateFrom")
    preferred_date_to: date = Field(alias="preferredDateTo")
    preferred_time_from: time = Field(alias="preferredTimeFrom")
    preferred_time_to: time = Field(alias="preferredTimeTo")
    sessions_required: int = Field(alias="sessionsRequired", ge=1)
    session_duration_minutes: int = Field(alias="sessionDurationMinutes", ge=30)
    required_equipment_type_ids: list[UUID] = Field(
        default_factory=list, alias="requiredEquipmentTypeIds"
    )
    rooms: list[SchedulingRoom] = Field(default_factory=list)


class SchedulingSlot(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    room_id: UUID = Field(alias="roomId")
    room_name: str = Field(alias="roomName")
    starts_at: datetime = Field(alias="startsAt")
    ends_at: datetime = Field(alias="endsAt")
    hourly_rate: float = Field(alias="hourlyRate")


class SchedulingResponse(BaseModel):
    """S2 output contract: `{ slots[], conflicts[] }`."""

    model_config = ConfigDict(populate_by_name=True)

    slots: list[SchedulingSlot] = Field(default_factory=list)
    conflicts: list[str] = Field(default_factory=list)
