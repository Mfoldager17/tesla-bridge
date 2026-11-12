"""Common base: one entity = one definition from /api/entities (same as MQTT discovery)."""

from __future__ import annotations

import json
from typing import Any

from homeassistant.const import EntityCategory
from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers.device_registry import DeviceInfo
from homeassistant.helpers.entity import Entity
from homeassistant.helpers.template import Template

from .api import BridgeError
from .const import BRIDGE


class BridgeEntity(Entity):
    """Reads fields from the hub and renders the same Jinja templates as the MQTT integration."""

    _attr_has_entity_name = True
    _attr_should_poll = False

    def __init__(self, hub: Any, vin: str, device: DeviceInfo, definition: dict[str, Any]) -> None:
        cfg: dict[str, Any] = definition["config"]
        self.hub = hub
        self.vin = vin
        self.cfg = cfg
        self._templates: dict[str, Template] = {}
        self._attr_unique_id = cfg["unique_id"]
        self._attr_name = cfg.get("name")
        self._attr_device_info = device
        self._attr_icon = cfg.get("icon")
        if cfg.get("entity_category"):
            self._attr_entity_category = EntityCategory(cfg["entity_category"])
        if cfg.get("enabled_by_default") is False:
            self._attr_entity_registry_enabled_default = False
        if cfg.get("default_entity_id"):
            self.entity_id = cfg["default_entity_id"]   # suggested id, e.g. sensor.model_y_battery_level

    # ---------- fields ----------

    def _ref(self, field: str) -> tuple[str, str] | None:
        topic = self.cfg.get(field)
        if not isinstance(topic, str):
            return None
        if topic == "usage":
            return (BRIDGE, "usage")
        if topic.startswith("state:"):
            return (self.vin, topic[6:])
        return None

    def raw(self, field: str) -> Any:
        ref = self._ref(field)
        return None if ref is None else self.hub.get(*ref)

    def _template(self, name: str) -> Template | None:
        source = self.cfg.get(name)
        if not source:
            return None
        if name not in self._templates:
            self._templates[name] = Template(source, self.hass)
        return self._templates[name]

    def render(self, field: str, template: str | None = None) -> str | None:
        """Field → text, exactly like MQTT does with value_json. "None"/empty = unknown."""
        value = self.raw(field)
        if value is None:
            return None   # unknown – like an MQTT topic without a value yet
        tpl = self._template(template) if template else None
        if tpl is None:
            return value if isinstance(value, str) else json.dumps(value)
        result = tpl.async_render_with_possible_json_value(json.dumps(value), None)
        if result is None:
            return None
        result = str(result).strip()
        return None if result in ("", "None", "none", "unknown") else result

    def render_command(self, template: str, value: Any) -> str:
        tpl = self._template(template)
        return str(value) if tpl is None else str(tpl.async_render({"value": value}, parse_result=False))

    async def send(self, field: str, payload: Any) -> None:
        target = self.cfg.get(field)
        if not isinstance(target, str) or not target.startswith("cmd:"):
            raise HomeAssistantError(f"{self.name} cannot be controlled")
        try:
            await self.hub.client.command(self.vin, target[4:], str(payload))
        except BridgeError as err:
            raise HomeAssistantError(f"Tesla Bridge: {err}") from err

    # ---------- waiting for the car ----------

    def _waiting(self) -> dict[str, Any] | None:
        """{pending, waiting_for} while a command waits for the car – the same attributes MQTT gets."""
        if self.cfg.get("json_attributes_topic") != "state:pending":
            return None
        rendered = self.render("json_attributes_topic", "json_attributes_template")
        try:
            attrs = json.loads(rendered) if rendered else None
        except ValueError:
            return None
        return attrs if isinstance(attrs, dict) else None

    @property
    def extra_state_attributes(self) -> dict[str, Any] | None:
        return self._waiting()

    @property
    def icon(self) -> str | None:
        waiting = self._waiting()
        if waiting and waiting.get("pending"):
            return "mdi:sleep" if waiting.get("waiting_for") == "car to wake up" else "mdi:timer-sand"
        return super().icon

    # ---------- lifecycle ----------

    @property
    def available(self) -> bool:
        return self.hub.connected

    async def async_added_to_hass(self) -> None:
        refs = {r for f in self.cfg if f.endswith("_topic") and (r := self._ref(f))}
        for vin, key in refs:
            self.async_on_remove(self.hub.subscribe(vin, key, self.async_write_ha_state))
        self.async_on_remove(self.hub.subscribe_availability(self.async_write_ha_state))


def to_float(value: str | None) -> float | None:
    if value is None:
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None
