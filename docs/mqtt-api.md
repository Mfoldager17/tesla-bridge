<sub>[T-Pitwall](../README.md) · [Guide](README.md) · Reference · MQTT API</sub>

# MQTT API (way A)

**State** (retained, JSON values, metric units):
```
t-pitwall/<VIN>/state/battery_level        72.5
t-pitwall/<VIN>/state/climate_on           true
t-pitwall/<VIN>/state/charging_state       "charging"
t-pitwall/<VIN>/state/gear                 "D"
t-pitwall/<VIN>/state/location             {"latitude":56.1,"longitude":10.1}
t-pitwall/<VIN>/state/locked_motion        "LOCKING"   (also *_motion for frunk, trunk, charge port and windows)
t-pitwall/<VIN>/json                       full snapshot with updated_at and source per field
t-pitwall/<VIN>/command/status             {"command":"ClimateOn","status":"waking",...}
t-pitwall/bridge/usage                     Fleet API usage for the month
t-pitwall/bridge/status                    online | offline
```
`source` is `stream` (confirmed by the car), `snapshot` (fetched from the Fleet API), `optimistic` (waiting for confirmation), `restored` (from disk) or `bridge`.

**Commands**: publish to `t-pitwall/<VIN>/cmd/<name>`:

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
mosquitto_pub -u t-pitwall -P '<password 1>' -t t-pitwall/<VIN>/cmd/climate -m on
```
