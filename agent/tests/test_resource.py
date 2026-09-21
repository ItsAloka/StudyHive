import uuid

from fastapi.testclient import TestClient

from app.agents import resource
from app.main import app
from app.schemas import ResourceRequest, ResourceRequestItem
from app.settings import settings

client = TestClient(app)

AUTH_HEADERS = {"X-Internal-Api-Key": settings.internal_api_key}


def _item(**overrides: object) -> dict[str, object]:
    body: dict[str, object] = {
        "consumableId": str(uuid.uuid4()),
        "name": "Whiteboard Marker",
        "requested": 5,
        "available": 20,
        "unitPrice": 1.5,
    }
    body.update(overrides)
    return body


def test_endpoint_requires_the_internal_api_key() -> None:
    response = client.post("/resource/prepare-reservation", json={"requestedItems": [_item()]})
    assert response.status_code == 401


def test_sufficient_stock_line_is_marked_sufficient_with_correct_line_total() -> None:
    response = client.post(
        "/resource/prepare-reservation",
        json={"requestedItems": [_item(requested=5, available=20, unitPrice=1.5)]},
        headers=AUTH_HEADERS,
    )

    assert response.status_code == 200
    body = response.json()
    assert body["allAvailable"] is True
    item = body["items"][0]
    assert item["sufficient"] is True
    assert item["lineTotal"] == 7.5
    assert body["totalCost"] == 7.5


def test_insufficient_stock_line_is_marked_not_sufficient_and_still_priced() -> None:
    response = client.post(
        "/resource/prepare-reservation",
        json={"requestedItems": [_item(requested=50, available=3, unitPrice=2.0)]},
        headers=AUTH_HEADERS,
    )

    assert response.status_code == 200
    body = response.json()
    assert body["allAvailable"] is False
    item = body["items"][0]
    assert item["sufficient"] is False
    # Priced at the requested quantity, not the available one — the shortfall is what
    # `sufficient`/`allAvailable` communicate, this is still "what it would have cost".
    assert item["lineTotal"] == 100.0


def test_one_insufficient_line_among_several_makes_the_whole_response_not_all_available() -> None:
    response = client.post(
        "/resource/prepare-reservation",
        json={
            "requestedItems": [
                _item(requested=1, available=10, unitPrice=1.0),
                _item(requested=99, available=1, unitPrice=1.0),
            ]
        },
        headers=AUTH_HEADERS,
    )

    body = response.json()
    assert [item["sufficient"] for item in body["items"]] == [True, False]
    assert body["allAvailable"] is False


def test_empty_requested_items_is_vacuously_all_available_with_zero_cost() -> None:
    response = client.post("/resource/prepare-reservation", json={"requestedItems": []}, headers=AUTH_HEADERS)

    assert response.status_code == 200
    body = response.json()
    assert body["items"] == []
    assert body["allAvailable"] is True
    assert body["totalCost"] == 0.0


def test_total_cost_sums_every_line_regardless_of_sufficiency() -> None:
    response = client.post(
        "/resource/prepare-reservation",
        json={
            "requestedItems": [
                _item(requested=2, available=10, unitPrice=3.0),  # 6.00, sufficient
                _item(requested=10, available=1, unitPrice=4.0),  # 40.00, insufficient
            ]
        },
        headers=AUTH_HEADERS,
    )

    assert response.json()["totalCost"] == 46.0


def test_exactly_enough_stock_counts_as_sufficient() -> None:
    """Boundary: available == requested must not be flagged insufficient."""
    response = client.post(
        "/resource/prepare-reservation",
        json={"requestedItems": [_item(requested=7, available=7, unitPrice=1.0)]},
        headers=AUTH_HEADERS,
    )

    assert response.json()["items"][0]["sufficient"] is True


def test_negative_requested_quantity_is_a_422_not_a_500() -> None:
    response = client.post(
        "/resource/prepare-reservation",
        json={"requestedItems": [_item(requested=-1)]},
        headers=AUTH_HEADERS,
    )
    assert response.status_code == 422


def test_missing_required_field_is_a_422_not_a_500() -> None:
    payload = _item()
    del payload["unitPrice"]

    response = client.post(
        "/resource/prepare-reservation", json={"requestedItems": [payload]}, headers=AUTH_HEADERS
    )
    assert response.status_code == 422


def test_line_total_rounds_to_cents() -> None:
    """3 * 0.1 is 0.30000000000000004 in raw floating point — must come back as a clean 0.30."""
    response = client.post(
        "/resource/prepare-reservation",
        json={"requestedItems": [_item(requested=3, available=10, unitPrice=0.1)]},
        headers=AUTH_HEADERS,
    )
    assert response.json()["items"][0]["lineTotal"] == 0.3


def _resource_request(*items: dict[str, object]) -> ResourceRequest:
    return ResourceRequest.model_validate({"requestedItems": list(items) or [_item()]})


class TestToolsDirectly:
    """Unit-level coverage of the individual tools, independent of the HTTP layer."""

    def test_check_stock_true_when_available_meets_requested(self) -> None:
        item = ResourceRequestItem.model_validate(_item(requested=5, available=5))
        assert resource.check_stock(item) is True

    def test_check_stock_false_when_available_below_requested(self) -> None:
        item = ResourceRequestItem.model_validate(_item(requested=6, available=5))
        assert resource.check_stock(item) is False

    def test_get_prices_passes_through_unit_price_unchanged(self) -> None:
        item = ResourceRequestItem.model_validate(_item(unitPrice=12.34))
        assert resource.get_prices(item) == 12.34

    def test_prepare_reservation_preserves_request_order(self) -> None:
        first_id = str(uuid.uuid4())
        second_id = str(uuid.uuid4())
        request = _resource_request(_item(consumableId=first_id), _item(consumableId=second_id))

        result = resource.prepare_reservation(request)

        assert [str(item.consumable_id) for item in result.items] == [first_id, second_id]
