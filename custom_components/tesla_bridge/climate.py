"""Climate: the cabin (on/off, temperature, Keep/Dog/Camp) and cabin overheat protection."""

from __future__ import annotations

from typing import Any

from homeassistant.components.climate import ClimateEntity, ClimateEntityFeature, HVACMode
from homeassistant.const import ATTR_TEMPERATURE, UnitOfTemperature
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from . import TeslaBridgeConfigEntry
from .entity import BridgeEntity, to_float


async def async_setup_entry(hass: HomeAssistant, entry: TeslaBridgeConfigEntry, async_add_entities: AddEntitiesCallback) -> None:
    async_add_entities(entry.runtime_data.build("climate", BridgeClimate))


class BridgeClimate(BridgeEntity, ClimateEntity):
    _attr_temperature_unit = UnitOfTemperature.CELSIUS

    def __init__(self, *args: Any) -> None:
        super().__init__(*args)
        cfg = self.cfg
        self._attr_hvac_modes = [HVACMode(m) for m in cfg.get("modes", ["off"])]
        self._attr_min_temp = cfg.get("min_temp", 15)
        self._attr_max_temp = cfg.get("max_temp", 28)
        self._attr_target_temperature_step = cfg.get("temp_step", 0.5)
        self._attr_precision = cfg.get("precision", 0.5)
        features = ClimateEntityFeature.TARGET_TEMPERATURE
        if HVACMode.OFF in self._attr_hvac_modes and len(self._attr_hvac_modes) > 1:
            features |= ClimateEntityFeature.TURN_ON | ClimateEntityFeature.TURN_OFF
        if cfg.get("preset_modes"):
            self._attr_preset_modes = list(cfg["preset_modes"])
            features |= ClimateEntityFeature.PRESET_MODE
        self._attr_supported_features = features

    @property
    def hvac_mode(self) -> HVACMode | None:
        value = self.render("mode_state_topic", "mode_state_template")
        return HVACMode(value) if value in self._attr_hvac_modes else None

    @property
    def target_temperature(self) -> float | None:
        return to_float(self.render("temperature_state_topic", "temperature_state_template"))

    @property
    def current_temperature(self) -> float | None:
        return to_float(self.render("current_temperature_topic", "current_temperature_template"))

    @property
    def preset_mode(self) -> str | None:
        if not self.cfg.get("preset_modes"):
            return None
        value = self.render("preset_mode_state_topic", "preset_mode_value_template")
        return value if value in self._attr_preset_modes else None

    async def async_set_hvac_mode(self, hvac_mode: HVACMode) -> None:
        await self.send("mode_command_topic", self.render_command("mode_command_template", str(hvac_mode)))

    async def async_turn_on(self) -> None:
        mode = next(m for m in self._attr_hvac_modes if m != HVACMode.OFF)
        await self.async_set_hvac_mode(mode)

    async def async_turn_off(self) -> None:
        await self.async_set_hvac_mode(HVACMode.OFF)

    async def async_set_temperature(self, **kwargs: Any) -> None:
        if (mode := kwargs.get("hvac_mode")) is not None:
            await self.async_set_hvac_mode(mode)
        if (temp := kwargs.get(ATTR_TEMPERATURE)) is not None:
            await self.send("temperature_command_topic", temp)

    async def async_set_preset_mode(self, preset_mode: str) -> None:
        await self.send("preset_mode_command_topic", preset_mode)
