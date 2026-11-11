"""Covers (frunk, trunk, charge port, windows) – with opening/closing while the car responds."""

from __future__ import annotations

from typing import Any

from homeassistant.components.cover import CoverDeviceClass, CoverEntity, CoverEntityFeature
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from .entity import BridgeEntity


async def async_setup_entry(hass: HomeAssistant, entry: Any, async_add_entities: AddEntitiesCallback) -> None:
    async_add_entities(entry.runtime_data.build("cover", BridgeCover))


class BridgeCover(BridgeEntity, CoverEntity):
    def __init__(self, *args: Any) -> None:
        super().__init__(*args)
        if self.cfg.get("device_class"):
            self._attr_device_class = CoverDeviceClass(self.cfg["device_class"])
        features = CoverEntityFeature(0)
        if self.cfg.get("payload_open"):
            features |= CoverEntityFeature.OPEN
        if self.cfg.get("payload_close"):
            features |= CoverEntityFeature.CLOSE
        self._attr_supported_features = features

    @property
    def _state(self) -> str | None:
        return self.render("state_topic", "value_template")

    @property
    def is_closed(self) -> bool | None:
        s = self._state
        if s == self.cfg.get("state_closed", "closed") or s == self.cfg.get("state_opening", "opening"):
            return True
        if s == self.cfg.get("state_open", "open") or s == self.cfg.get("state_closing", "closing"):
            return False
        return None

    @property
    def is_opening(self) -> bool:
        return self._state == self.cfg.get("state_opening", "opening")

    @property
    def is_closing(self) -> bool:
        return self._state == self.cfg.get("state_closing", "closing")

    async def async_open_cover(self, **kwargs: Any) -> None:
        await self.send("command_topic", self.cfg["payload_open"])

    async def async_close_cover(self, **kwargs: Any) -> None:
        await self.send("command_topic", self.cfg["payload_close"])
