"""Switches (charging, sentry, defrost, guest mode …)."""

from __future__ import annotations

from typing import Any

from homeassistant.components.switch import SwitchEntity
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from . import TeslaBridgeConfigEntry
from .entity import BridgeEntity


async def async_setup_entry(hass: HomeAssistant, entry: TeslaBridgeConfigEntry, async_add_entities: AddEntitiesCallback) -> None:
    async_add_entities(entry.runtime_data.build("switch", BridgeSwitch))


class BridgeSwitch(BridgeEntity, SwitchEntity):
    @property
    def is_on(self) -> bool | None:
        value = self.render("state_topic", "value_template")
        if value == self.cfg.get("state_on", "ON"):
            return True
        if value == self.cfg.get("state_off", "OFF"):
            return False
        return None

    async def async_turn_on(self, **kwargs: Any) -> None:
        await self.send("command_topic", self.cfg.get("payload_on", "on"))

    async def async_turn_off(self, **kwargs: Any) -> None:
        await self.send("command_topic", self.cfg.get("payload_off", "off"))
