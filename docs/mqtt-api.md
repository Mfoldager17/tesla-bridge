<sub>[Tesla Bridge](../README.md) · [Guide](README.md) · Reference · MQTT API</sub>

# MQTT API (way A)

**State** (retained, JSON values, metric units):
```
tesla/<VIN>/state/battery_level        72.5
tesla/<VIN>/state/climate_on           true
tesla/<VIN>/state/charging_state       "charging"
tesla/<VIN>/state/gear                 "D"
tesla/<VIN>/state/location             {"latitude":56.1,"longitude":10.1}
tesla/<VIN>/state/locked_motion        "LOCKING"   (also *_motion for frunk, trunk, charge port and windows)
tesla/<VIN>/json                       full snapshot with updated_at and source per field
tesla/<VIN>/command/status             {"command":"ClimateOn","status":"waking",...}
tesla/bridge/usage                     Fleet API usage for the month
tesla/bridge/status                    online | offline
```
`source` is `stream` (confirmed by the car), `snapshot` (fetched from the Fleet API), `optimistic` (waiting for confirmation), `restored` (from disk) or `bridge`.

**Commands**: publish to `tesla/<VIN>/cmd/<name>`:

| Name | Payload |
|---|---|
| `climate`, `defrost`, `charge`, `sentry_mode`, `guest_mode`, `valet_mode`, `steering_wheel_heat`, `auto_seat_climate_left/right`, `auto_steering_wheel_heater` | `on` / `off` |
| `lock` | `LOCK` / `UNLOCK` |
| `trunk`, `windows`, `charge_port_door` | `OPEN` / `CLOSE` |
| `frunk` | `OPEN` |
| `charge_cable_lock` | `UNLOCK` (opens the charge port and releases the cable) |
| `driver_temp` | `21.5` (15–28) |
| `climate_keeper` | `off` `keep` `dog` `camp` |
| `cabin_overheat_protection` | `off` `on` `fan_only` |
| `cop_temp` | `30` `35` `40` |
| `charge_limit` / `charge_current` | `80` / `16` |
| `seat_heater_left/right`, `seat_heater_rear_left/center/right` | `off` `low` `medium` `high` or `0`–`3` |
| `steering_wheel_heater` | `off` `low` `high` |
| `wake`, `flash_lights`, `honk_horn`, `homelink`, `keyless_driving`, `play_fart` | anything |

```bash
mosquitto_pub -u tesla-bridge -P '<password 1>' -t tesla/<VIN>/cmd/climate -m on
```
