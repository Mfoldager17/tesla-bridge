"""Buttons (wake, flash lights, honk, homelink …)."""

from __future__ import annotations

from typing import Any

from homeassistant.components.button import ButtonEntity
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from .entity import BridgeEntity


async def async_setup_entry(hass: HomeAssistant, entry: Any, async_add_entities: AddEntitiesCallback) -> None:
    async_add_entities(entry.runtime_data.build("button", BridgeButton))


class BridgeButton(BridgeEntity, ButtonEntity):
    async def async_press(self) -> None:
        await self.send("command_topic", self.cfg.get("payload_press", "PRESS"))
