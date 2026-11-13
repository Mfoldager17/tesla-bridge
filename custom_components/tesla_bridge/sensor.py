"""Sensors (battery, range, temperatures, arrival, usage …)."""

from __future__ import annotations

from typing import Any

from homeassistant.components.sensor import SensorDeviceClass, SensorEntity, SensorStateClass
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddEntitiesCallback
from homeassistant.util import dt as dt_util

from . import TeslaBridgeConfigEntry
from .entity import BridgeEntity, to_float


async def async_setup_entry(hass: HomeAssistant, entry: TeslaBridgeConfigEntry, async_add_entities: AddEntitiesCallback) -> None:
    async_add_entities(entry.runtime_data.build("sensor", BridgeSensor))


class BridgeSensor(BridgeEntity, SensorEntity):
    def __init__(self, *args: Any) -> None:
        super().__init__(*args)
        cfg = self.cfg
        self._attr_native_unit_of_measurement = cfg.get("unit_of_measurement")
        if cfg.get("device_class"):
            self._attr_device_class = SensorDeviceClass(cfg["device_class"])
        if cfg.get("state_class"):
            self._attr_state_class = SensorStateClass(cfg["state_class"])

    @property
    def native_value(self) -> Any:
        value = self.render("state_topic", "value_template")
        if value is None:
            return None
        if self.device_class == SensorDeviceClass.TIMESTAMP:
            return dt_util.parse_datetime(value)
        if self.cfg.get("unit_of_measurement") or self.cfg.get("state_class"):
            return to_float(value)
        return value

    @property
    def extra_state_attributes(self) -> dict[str, Any] | None:
        attrs = self.raw("json_attributes_topic")
        return attrs if isinstance(attrs, dict) else None
