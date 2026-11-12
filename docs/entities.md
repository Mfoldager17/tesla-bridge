<sub>[Tesla Bridge](../README.md) · [Guide](README.md) · Reference · Entities</sub>

# Entities in Home Assistant

In HA, the car is called whatever you put in `Bridge__Vehicles__0__Name` in `.env` (e.g. "Model Y", "My Car" or "Elsa"), and that name becomes the prefix of every entity id: "Model Y" → `sensor.model_y_battery_level`, "My Car" → `sensor.my_car_battery_level`. In the examples here, the car is called "Model Y".

Same names and types as Teslemetry. The prefix comes from `Bridge__Vehicles__0__Name`: "Model Y" gives `model_y_`, "My Car" gives `my_car_`. Accented and Nordic letters are simplified (æ → ae, ø → oe, å → a, é → e). The table shows the names without the prefix.

For more cars, add `Bridge__Vehicles__1__Vin` and `Bridge__Vehicles__1__Name` and so on. Each car becomes its own device with its own prefix.

| Type | Entities |
|---|---|
| `climate` | `climate` (on/off, temperature, presets off/keep/dog/camp), `cabin_overheat_protection` (off/cool/fan only, 30/35/40 °C) |
| `lock` | `lock`, `charge_cable_lock` |
| `cover` | `frunk` (open only), `trunk`, `charge_port_door`, `windows` |
| `switch` | `charge`, `sentry_mode`, `defrost`, `guest_mode`, `valet_mode`, `auto_seat_climate_left/right`, `auto_steering_wheel_heater` |
| `number` | `charge_limit`, `charge_current` |
| `select` | `seat_heater_front_left/right`, `seat_heater_rear_left/center/right`, `steering_wheel_heater` |
| `button` | `wake`, `flash_lights`, `honk_horn`, `homelink`, `keyless_driving`, `play_fart` |
| `sensor` | `battery_level`, `charging`, `charger_power/voltage/current`, `charge_energy_added`, `time_to_full_charge`, `energy_remaining`, `estimate_battery_range`, `ideal_battery_range`, `speed`, `shift_state` (P/D/R/N), `odometer`, `tire_pressure_*`, `inside/outside_temperature`, `destination`, `time_to_arrival`, `distance_to_arrival`, `state_of_charge_at_arrival`, `version`, `last_seen`, `command_status` … |
| `binary_sensor` | `status` (online), `charge_cable`, doors, windows, `driver_seat_occupied`, `driver_seat_belt`, `preconditioning_enabled`, `scheduled_charging_pending`, `fast_charger_present`, `bluetooth` (only with an [ESP32](ble.md): on while the car is in Bluetooth range) |
| `device_tracker` | `location` (the car), `route` (destination from the navigation) |

There is also a **Tesla Bridge** device with your Fleet API usage: `sensor.tesla_bridge_cost_month`, `…_cost_projected`, `…_credit_left`, plus counts of signals, commands, wakes and data requests for the month. Handy for an automation that warns you if the projected cost approaches $10.

`sensor.<car>_command_status` shows the latest command (`pending` → `waking` → `succeeded`/`failed`) with the error as an attribute.

The names follow Teslemetry, so moving from there only means changing the entity id prefix (`sensor.tesla_battery_level` → `sensor.model_y_battery_level`). To avoid even that, give the car the same name it has in Teslemetry.
