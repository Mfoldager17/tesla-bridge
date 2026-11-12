"""Runs against a real bridge (dotnet run, Development + simulator) at BRIDGE_URL."""

import asyncio
import os

import aiohttp
import pytest
from pytest_homeassistant_custom_component.common import MockConfigEntry

from homeassistant.core import HomeAssistant

URL = os.environ.get("BRIDGE_URL", "http://127.0.0.1:8080")
VIN = "SIMULERET00000001"


@pytest.mark.allow_hosts(["127.0.0.1"])
async def test_entities_follow_the_bridge(hass: HomeAssistant, socket_enabled) -> None:
    entry = MockConfigEntry(domain="tesla_bridge", data={"url": URL, "token": None}, unique_id=VIN)
    entry.add_to_hass(hass)
    assert await hass.config_entries.async_setup(entry.entry_id)
    await hass.async_block_till_done()

    states = hass.states.async_all()
    ids = {s.entity_id for s in states}
    print(len(ids), "entities")
    for e in ["sensor.model_y_battery_level", "lock.model_y_lock", "climate.model_y_climate",
              "cover.model_y_frunk", "device_tracker.model_y_location", "select.model_y_seat_heater_front_left",
              "number.model_y_charge_limit", "switch.model_y_charge", "button.model_y_wake",
              "binary_sensor.model_y_status", "sensor.tesla_bridge_cost_month",
              "climate.model_y_cabin_overheat_protection"]:
        assert e in ids, e

    async with aiohttp.ClientSession() as s:
        async with s.post(f"{URL}/api/vehicles/{VIN}/simulate") as r:
            await r.read()
    for _ in range(40):
        await asyncio.sleep(0.1)
        await hass.async_block_till_done()
        if hass.states.get("sensor.model_y_battery_level").state not in ("unknown", "unavailable"):
            break

    bat = hass.states.get("sensor.model_y_battery_level")
    print("battery", bat.state, bat.attributes)
    assert float(bat.state) > 0
    assert hass.states.get("sensor.model_y_charging").state == "charging"
    assert hass.states.get("switch.model_y_charge").state == "on"
    assert hass.states.get("lock.model_y_lock").state in ("locked", "unlocked")
    assert hass.states.get("cover.model_y_frunk").state == "closed"
    loc = hass.states.get("device_tracker.model_y_location")
    assert loc.attributes["latitude"] == 56.1185
    clim = hass.states.get("climate.model_y_climate")
    print("climate", clim.state, clim.attributes)
    assert clim.state in ("heat_cool", "off")
    assert clim.attributes["current_temperature"] is not None
    print("cost", hass.states.get("sensor.tesla_bridge_cost_month"))
    assert hass.states.get("sensor.model_y_last_seen").state not in ("unknown", "unavailable")

    # Command: unlock → the bridge sets an optimistic state → UNLOCKING/unlocked via SSE
    before = hass.states.get("lock.model_y_lock").state
    service = "unlock" if before == "locked" else "lock"
    await hass.services.async_call("lock", service, {"entity_id": "lock.model_y_lock"}, blocking=True)
    seen = set()
    for _ in range(30):
        await asyncio.sleep(0.1)
        await hass.async_block_till_done()
        seen.add(hass.states.get("lock.model_y_lock").state)
    print("lock states seen", before, seen)
    status = hass.states.get("sensor.model_y_command_status")
    assert status.attributes.get("command") in ("Lock", "Unlock"), status
    print("command_status", hass.states.get("sensor.model_y_command_status"))

    await hass.services.async_call("climate", "set_preset_mode",
                                   {"entity_id": "climate.model_y_climate", "preset_mode": "dog"}, blocking=True)
    await hass.services.async_call("climate", "set_hvac_mode",
                                   {"entity_id": "climate.model_y_cabin_overheat_protection", "hvac_mode": "fan_only"}, blocking=True)
    await hass.services.async_call("select", "select_option",
                                   {"entity_id": "select.model_y_seat_heater_front_left", "option": "high"}, blocking=True)
    await hass.services.async_call("number", "set_value",
                                   {"entity_id": "number.model_y_charge_limit", "value": 80}, blocking=True)
    for _ in range(10):
        await asyncio.sleep(0.1)
        await hass.async_block_till_done()
    print("preset", hass.states.get("climate.model_y_climate").attributes.get("preset_mode"))
    print("cop", hass.states.get("climate.model_y_cabin_overheat_protection").state)
    print("seat", hass.states.get("select.model_y_seat_heater_front_left").state)
    print("limit", hass.states.get("number.model_y_charge_limit").state)
    # Without a Tesla login the commands fail in the bridge; what matters here is that they arrived.
    status = hass.states.get("sensor.model_y_command_status")
    print("status", status.state, status.attributes)
    assert status.attributes.get("command") == "SetChargeLimit"

    assert await hass.config_entries.async_unload(entry.entry_id)


@pytest.mark.allow_hosts(["127.0.0.1"])
async def test_waiting_for_the_car_shows_on_the_entities(hass: HomeAssistant, socket_enabled) -> None:
    entry = MockConfigEntry(domain="tesla_bridge", data={"url": URL, "token": None}, unique_id=VIN)
    entry.add_to_hass(hass)
    assert await hass.config_entries.async_setup(entry.entry_id)
    await hass.async_block_till_done()
    hub = entry.runtime_data
    for _ in range(50):   # wait for the live connection
        await asyncio.sleep(0.1)
        await hass.async_block_till_done()
        if hub.connected:
            break

    # What the bridge publishes while it wakes the car for "climate on"
    hub.set_value(VIN, "pending", {"waking": True, "keys": ["climate_on"]})
    await hass.async_block_till_done()
    clim = hass.states.get("climate.model_y_climate")
    print("climate while waking", clim.state, clim.attributes)
    assert clim.attributes["pending"] is True
    assert clim.attributes["waiting_for"] == "car to wake up"
    assert clim.attributes["icon"] == "mdi:sleep"
    other = hass.states.get("switch.model_y_sentry_mode")
    assert other.attributes["pending"] is False and other.attributes.get("waiting_for") is None

    hub.set_value(VIN, "pending", {"waking": False, "keys": ["climate_on"]})
    await hass.async_block_till_done()
    clim = hass.states.get("climate.model_y_climate")
    assert clim.attributes["waiting_for"] == "car to confirm"
    assert clim.attributes["icon"] == "mdi:timer-sand"

    hub.set_value(VIN, "pending", {"waking": False, "keys": []})
    await hass.async_block_till_done()
    assert hass.states.get("climate.model_y_climate").attributes["pending"] is False
    await hass.config_entries.async_unload(entry.entry_id)


@pytest.mark.allow_hosts(["127.0.0.1"])
async def test_config_flow(hass: HomeAssistant, socket_enabled) -> None:
    result = await hass.config_entries.flow.async_init("tesla_bridge", context={"source": "user"})
    assert result["type"] == "form"
    bad = await hass.config_entries.flow.async_configure(result["flow_id"], {"url": "127.0.0.1:1"})
    assert bad["errors"] == {"base": "cannot_connect"}
    ok = await hass.config_entries.flow.async_configure(result["flow_id"], {"url": URL.removeprefix("http://")})
    assert ok["type"] == "create_entry", ok
    assert ok["title"] == "Model Y"
    assert ok["data"]["url"] == URL
    await hass.async_block_till_done()
    assert hass.states.get("sensor.model_y_battery_level") is not None
    await hass.config_entries.async_unload(ok["result"].entry_id)
