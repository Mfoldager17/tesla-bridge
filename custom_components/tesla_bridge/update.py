"""Update entity: a new Tesla Bridge release, shown under Settings → Updates."""

from __future__ import annotations

from typing import Any

from homeassistant.components.update import UpdateDeviceClass, UpdateEntity, UpdateEntityFeature
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from . import TeslaBridgeConfigEntry
from .entity import BridgeEntity


async def async_setup_entry(hass: HomeAssistant, entry: TeslaBridgeConfigEntry, add: AddEntitiesCallback) -> None:
    add(entry.runtime_data.build("update", BridgeUpdate))


class BridgeUpdate(BridgeEntity, UpdateEntity):
    """No install button: the bridge runs in Docker, so it is updated with `docker compose pull`."""

    _attr_device_class = UpdateDeviceClass.FIRMWARE
    _attr_supported_features = UpdateEntityFeature.RELEASE_NOTES

    def _data(self) -> dict[str, Any]:
        value = self.raw("state_topic")
        return value if isinstance(value, dict) else {}

    @property
    def installed_version(self) -> str | None:
        return self._data().get("installed_version")

    @property
    def latest_version(self) -> str | None:
        return self._data().get("latest_version") or self.installed_version

    @property
    def title(self) -> str | None:
        return self._data().get("title") or "Tesla Bridge"

    @property
    def release_url(self) -> str | None:
        return self._data().get("release_url")

    @property
    def release_summary(self) -> str | None:
        return self._data().get("release_summary")

    async def async_release_notes(self) -> str | None:
        notes = self._data().get("release_notes") or ""
        if self.latest_version == self.installed_version:
            return notes or None
        return notes + (
            "\n\n---\n**Update the bridge** on the server, in its folder:\n\n"
            "```\ndocker compose pull && docker compose up -d\n```"
        )
