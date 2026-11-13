"""Numbers (charge limit and charge current)."""

from __future__ import annotations

from typing import Any

from homeassistant.components.number import NumberEntity, NumberMode
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from . import TeslaBridgeConfigEntry
from .entity import BridgeEntity, to_float


async def async_setup_entry(hass: HomeAssistant, entry: TeslaBridgeConfigEntry, async_add_entities: AddEntitiesCallback) -> None:
    async_add_entities(entry.runtime_data.build("number", BridgeNumber))


class BridgeNumber(BridgeEntity, NumberEntity):
    def __init__(self, *args: Any) -> None:
        super().__init__(*args)
        cfg = self.cfg
        self._attr_native_min_value = cfg.get("min", 0)
        self._attr_native_max_value = cfg.get("max", 100)
        self._attr_native_step = cfg.get("step", 1)
        self._attr_native_unit_of_measurement = cfg.get("unit_of_measurement")
        self._attr_mode = NumberMode(cfg.get("mode", "auto"))

    @property
    def native_value(self) -> float | None:
        return to_float(self.render("state_topic", "value_template"))

    async def async_set_native_value(self, value: float) -> None:
        await self.send("command_topic", int(value) if float(value).is_integer() else value)
