"""Låse (bilen og ladekablet) – med LOCKING/UNLOCKING mens bilen svarer."""

from __future__ import annotations

from typing import Any

from homeassistant.components.lock import LockEntity
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from .entity import BridgeEntity


async def async_setup_entry(hass: HomeAssistant, entry: Any, async_add_entities: AddEntitiesCallback) -> None:
    async_add_entities(entry.runtime_data.build("lock", BridgeLock))


class BridgeLock(BridgeEntity, LockEntity):
    @property
    def _state(self) -> str | None:
        return self.render("state_topic", "value_template")

    @property
    def is_locked(self) -> bool | None:
        s = self._state
        return None if s is None else s in ("LOCKED", "UNLOCKING")

    @property
    def is_locking(self) -> bool:
        return self._state == "LOCKING"

    @property
    def is_unlocking(self) -> bool:
        return self._state == "UNLOCKING"

    async def async_lock(self, **kwargs: Any) -> None:
        await self.send("command_topic", self.cfg.get("payload_lock", "LOCK"))

    async def async_unlock(self, **kwargs: Any) -> None:
        await self.send("command_topic", self.cfg.get("payload_unlock", "UNLOCK"))
