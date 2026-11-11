"""Selects (seat heaters and steering wheel heater)."""

from __future__ import annotations

from typing import Any

from homeassistant.components.select import SelectEntity
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from .entity import BridgeEntity


async def async_setup_entry(hass: HomeAssistant, entry: Any, async_add_entities: AddEntitiesCallback) -> None:
    async_add_entities(entry.runtime_data.build("select", BridgeSelect))


class BridgeSelect(BridgeEntity, SelectEntity):
    def __init__(self, *args: Any) -> None:
        super().__init__(*args)
        self._attr_options = list(self.cfg.get("options", []))

    @property
    def current_option(self) -> str | None:
        value = self.render("state_topic", "value_template")
        return value if value in self._attr_options else None

    async def async_select_option(self, option: str) -> None:
        await self.send("command_topic", option)
