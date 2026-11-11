"""Position og rute (destination) som device trackers."""

from __future__ import annotations

from typing import Any

from homeassistant.components.device_tracker import SourceType, TrackerEntity
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from .entity import BridgeEntity


async def async_setup_entry(hass: HomeAssistant, entry: Any, async_add_entities: AddEntitiesCallback) -> None:
    async_add_entities(entry.runtime_data.build("device_tracker", BridgeTracker))


class BridgeTracker(BridgeEntity, TrackerEntity):
    _attr_source_type = SourceType.GPS

    @property
    def _position(self) -> dict[str, Any]:
        value = self.raw("json_attributes_topic")
        return value if isinstance(value, dict) else {}

    @property
    def latitude(self) -> float | None:
        return self._position.get("latitude")

    @property
    def longitude(self) -> float | None:
        return self._position.get("longitude")

    @property
    def extra_state_attributes(self) -> dict[str, Any]:
        return {k: v for k, v in self._position.items() if k not in ("latitude", "longitude", "gps_accuracy")}
