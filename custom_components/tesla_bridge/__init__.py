"""Tesla Bridge – Home Assistant-integration uden MQTT.

Taler direkte med bridgens HTTP-API (samme som dashboardet på :8090):
  GET  /api/entities   entiteterne (de samme som MQTT discovery – navne, typer og templates)
  GET  /api/state      alle felter pr. bil
  GET  /api/events     Server-Sent Events: ændringer live (push, ingen polling)
  POST /api/vehicles/{vin}/cmd/{navn}   kommandoer
"""

from __future__ import annotations

import asyncio
from collections.abc import Callable
from datetime import timedelta
import logging
from typing import Any

from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant, callback
from homeassistant.exceptions import ConfigEntryAuthFailed, ConfigEntryNotReady
from homeassistant.helpers.aiohttp_client import async_get_clientsession
from homeassistant.helpers.device_registry import DeviceInfo
from homeassistant.helpers.event import async_track_time_interval

from .api import BridgeAuthError, BridgeClient, BridgeError
from .const import BRIDGE, CONF_TOKEN, CONF_URL, DOMAIN, PLATFORMS

_LOGGER = logging.getLogger(__name__)

type TeslaBridgeConfigEntry = ConfigEntry[BridgeHub]


class BridgeHub:
    """Holder bridgens tilstand i hukommelsen og giver entiteterne besked, når et felt ændrer sig."""

    def __init__(self, hass: HomeAssistant, client: BridgeClient) -> None:
        self.hass = hass
        self.client = client
        self.definitions: dict[str, Any] = {}
        self.state: dict[str, dict[str, Any]] = {}
        self.connected = False
        self._listeners: dict[tuple[str, str], set[Callable[[], None]]] = {}
        self._availability: set[Callable[[], None]] = set()
        self._stop = asyncio.Event()

    # ---------- indlæsning ----------

    async def async_load(self) -> None:
        self.definitions = await self.client.entities()
        await self.async_refresh_state()
        await self.async_refresh_usage()

    async def async_refresh_state(self) -> None:
        for car in await self.client.state():
            vin = car["vin"]
            fields = {k: f.get("value") for k, f in (car.get("fields") or {}).items()}
            old = self.state.get(vin, {})
            self.state[vin] = {**({"command_status": old["command_status"]} if "command_status" in old else {}), **fields}
            for key in set(old) | set(fields):
                if old.get(key) != self.state[vin].get(key):
                    self._notify(vin, key)

    async def async_refresh_usage(self, *_: Any) -> None:
        try:
            self.set_value(BRIDGE, "usage", await self.client.usage())
        except BridgeError as err:
            _LOGGER.debug("Kunne ikke hente forbrug: %s", err)

    # ---------- abonnementer ----------

    def get(self, vin: str, key: str) -> Any:
        return self.state.get(vin, {}).get(key)

    @callback
    def set_value(self, vin: str, key: str, value: Any) -> None:
        car = self.state.setdefault(vin, {})
        if key in car and car[key] == value:
            return
        car[key] = value
        self._notify(vin, key)

    @callback
    def subscribe(self, vin: str, key: str, cb: Callable[[], None]) -> Callable[[], None]:
        self._listeners.setdefault((vin, key), set()).add(cb)
        return lambda: self._listeners.get((vin, key), set()).discard(cb)

    @callback
    def subscribe_availability(self, cb: Callable[[], None]) -> Callable[[], None]:
        self._availability.add(cb)
        return lambda: self._availability.discard(cb)

    def _notify(self, vin: str, key: str) -> None:
        for cb in list(self._listeners.get((vin, key), ())):
            cb()

    def _set_connected(self, connected: bool) -> None:
        if connected == self.connected:
            return
        self.connected = connected
        for cb in list(self._availability):
            cb()

    # ---------- live-hændelser ----------

    async def async_run(self) -> None:
        """Lytter på /api/events og forbinder igen, hvis forbindelsen ryger."""
        delay = 1
        while not self._stop.is_set():
            try:
                await self.async_refresh_state()   # indhent alt, der er sket mens vi var væk
                events = self.client.events()
                self._set_connected(True)
                delay = 1
                async for event in events:
                    self._handle(event)
                raise BridgeError("forbindelsen blev lukket")
            except asyncio.CancelledError:
                raise
            except BridgeAuthError:
                _LOGGER.error("Tesla Bridge afviste token – ret det under integrationen")
                self._set_connected(False)
                delay = 60
            except Exception as err:  # noqa: BLE001 – alt skal give et nyt forsøg
                if self.connected:
                    _LOGGER.warning("Mistede forbindelsen til Tesla Bridge: %s", err)
                self._set_connected(False)
            try:
                await asyncio.wait_for(self._stop.wait(), delay)
            except TimeoutError:
                pass
            delay = min(delay * 2, 30)

    @callback
    def _handle(self, event: dict[str, Any]) -> None:
        vin = event.get("vin")
        data = event.get("data") or {}
        if not vin:
            return
        if event.get("type") == "field" and "key" in data:
            self.set_value(vin, data["key"], data.get("value"))
        elif event.get("type") == "command":
            self.set_value(vin, "command_status", data)

    def stop(self) -> None:
        self._stop.set()

    # ---------- entiteter ----------

    def build(self, platform: str, factory: Callable[..., Any]) -> list[Any]:
        """Laver alle entiteter af én type ud fra /api/entities."""
        result = []
        for car in self.definitions.get("vehicles", []):
            device = DeviceInfo(
                identifiers={(DOMAIN, car["vin"])},
                name=car["name"],
                manufacturer="Tesla",
                model="Tesla Bridge",
                serial_number=car["vin"],
                configuration_url=self.client.url,
            )
            for d in car.get("entities", []):
                if d["platform"] == platform:
                    result.append(factory(self, car["vin"], device, d))
        bridge = DeviceInfo(
            identifiers={(DOMAIN, "bridge")},
            name="Tesla Bridge",
            manufacturer="Tesla Bridge",
            model="Fleet API-forbrug",
            configuration_url=self.client.url,
        )
        for d in self.definitions.get("bridge", {}).get("entities", []):
            if d["platform"] == platform:
                result.append(factory(self, BRIDGE, bridge, d))
        return result


async def async_setup_entry(hass: HomeAssistant, entry: TeslaBridgeConfigEntry) -> bool:
    client = BridgeClient(async_get_clientsession(hass), entry.data[CONF_URL], entry.data.get(CONF_TOKEN))
    hub = BridgeHub(hass, client)
    try:
        await hub.async_load()
    except BridgeAuthError as err:
        raise ConfigEntryAuthFailed(str(err)) from err
    except BridgeError as err:
        raise ConfigEntryNotReady(f"Kan ikke nå Tesla Bridge på {client.url}: {err}") from err

    entry.runtime_data = hub
    await hass.config_entries.async_forward_entry_setups(entry, PLATFORMS)

    entry.async_create_background_task(hass, hub.async_run(), "tesla_bridge_events")
    entry.async_on_unload(async_track_time_interval(hass, hub.async_refresh_usage, timedelta(minutes=5)))
    entry.async_on_unload(hub.stop)
    return True


async def async_unload_entry(hass: HomeAssistant, entry: TeslaBridgeConfigEntry) -> bool:
    return await hass.config_entries.async_unload_platforms(entry, PLATFORMS)
