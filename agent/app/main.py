from fastapi import APIRouter, Depends, FastAPI

from app.agents.planner import plan as run_planner
from app.agents.scheduling import schedule as run_scheduling
from app.schemas import PlannerRequest, PlannerResponse, SchedulingRequest, SchedulingResponse
from app.agents.resource import prepare_reservation as run_resource_prepare_reservation
from app.schemas import PlannerRequest, PlannerResponse, ResourceRequest, ResourceResponse
from app.security import require_internal_api_key
from app.settings import settings

app = FastAPI(
    title="StudyHive Agent Service",
    description=(
        "Internal FastAPI + LangGraph service. Reachable only from the ASP.NET Core API — "
        "never expose this service directly to React or Flutter."
    ),
    version="0.1.0",
)


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "healthy"}


# Every route on this router requires the X-Internal-Api-Key header (see app/security.py). Add
# future agent endpoints here, not directly on `app`, so the internal-only boundary can't be
# forgotten one route at a time.
internal_router = APIRouter(dependencies=[Depends(require_internal_api_key)])


@internal_router.get("/config/limits")
def workflow_limits() -> dict[str, int]:
    """Exposes the active timeout/retry limits so the API and agent service can be verified in sync."""
    return {
        "toolCallTimeoutSeconds": settings.tool_call_timeout_seconds,
        "singleAgentTimeoutSeconds": settings.single_agent_timeout_seconds,
        "wholeWorkflowTimeoutSeconds": settings.whole_workflow_timeout_seconds,
        "maxRetriesPerStep": settings.max_retries_per_step,
        "maxLlmTokensPerRun": settings.max_llm_tokens_per_run,
    }


@internal_router.post("/planner/plan", response_model=PlannerResponse)
def planner_plan(request: PlannerRequest) -> PlannerResponse:
    """S1's Planner Agent — see app/agents/planner.py for the tool sequence and DOCS §11 for the contract."""
    return run_planner(request)


@internal_router.post("/scheduling/propose", response_model=SchedulingResponse)
def scheduling_propose(request: SchedulingRequest) -> SchedulingResponse:
    """S2's Scheduling Agent — filters rooms, checks conflicts and proposes slots."""
    return run_scheduling(request)


@internal_router.post("/resource/prepare-reservation", response_model=ResourceResponse)
def resource_prepare_reservation(request: ResourceRequest) -> ResourceResponse:
    """S3's Resource Agent — see app/agents/resource.py for the tool sequence and DOCS §11 for the contract."""
    return run_resource_prepare_reservation(request)


app.include_router(internal_router)
