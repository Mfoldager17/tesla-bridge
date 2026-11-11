"""Binære sensorer (status, døre, vinduer, ladekabel …)."""

from __future__ import annotations

from typing import Any

from homeassistant.components.binary_sensor import BinarySensorDeviceClass, BinarySensorEntity
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from .entity import BridgeEntity


async def async_setup_entry(hass: HomeAssistant, entry: Any, async_add_entities: AddEntitiesCallback) -> None:
    async_add_entities(entry.runtime_data.build("binary_sensor", BridgeBinarySensor))


class BridgeBinarySensor(BridgeEntity, BinarySensorEntity):
    def __init__(self, *args: Any) -> None:
        super().__init__(*args)
        if self.cfg.get("device_class"):
            self._attr_device_class = BinarySensorDeviceClass(self.cfg["device_class"])

    @property
    def is_on(self) -> bool | None:
        value = self.render("state_topic", "value_template")
        return {"ON": True, "OFF": False}.get(value or "")
