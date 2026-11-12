<sub>[Tesla Bridge](../README.md) · [Guide](README.md) · Reference · HTTP API</sub>

# HTTP API

This is the API the integration and the dashboard use. It runs on port 8090 in both ways, so ESP32 panels, Node-RED etc. can use it without MQTT too.
Token: `Authorization: Bearer <token>` (or `?token=` in the URL).

| Endpoint | |
|---|---|
| `GET /api/info` | start time, cars, whether MQTT is connected, whether a token is required (needs no token itself) |
| `GET /api/entities` | all HA entities: the same definitions as MQTT discovery, with `state:<field>` / `cmd:<name>` instead of topics |
| `GET /api/state` | all fields per car with value, timestamp and source (including `*_motion`) |
| `GET /api/events` | Server-Sent Events: `field` (a value changed) and `command` (command status), live |
| `POST /api/vehicles/<VIN>/cmd/<name>` | a command. Body = the same payload as on MQTT (empty body = `PRESS`). Answers `202` straight away; with `?wait=true` it answers when the car has done it (woken first if needed): `200`, or `409`/`502` with `{"error": …}` |
| `GET /api/usage` | Fleet API usage |
| `GET /api/settings` | telemetry fields with intervals, on/off and this month's signal count, plus the snapshot interval |
| `PUT /api/settings` | change them: `{"fields": {"Location": {"enabled": true, "interval_seconds": 10}}, "snapshot_interval_minutes": 60}` – saved and sent to the car |
| `POST /api/settings/reset` | back to the defaults |
| `GET /api/car-model/info` | the chosen car view and which models exist |
| `GET /api/car-model` · `/api/car-model/builtin` | your uploaded model, or the bundled Model Y |
| `POST /api/car-model` · `DELETE /api/car-model` | upload (body = the .glb) or remove the uploaded model |
| `GET /api/ble` | per car: the [ESP32](ble.md) address, whether it's reachable, whether the car is in Bluetooth range, signal, the car's Bluetooth name |
| `PUT /api/ble/<VIN>` · `DELETE /api/ble/<VIN>` | set the ESP32: `{"url": "http://192.168.1.50", "username": null, "password": null}` (password left out = unchanged), or forget the dashboard setting |
| `POST /api/ble/<VIN>/pair` · `/wake` | press *Pair BLE key* (then tap the key card in the car) or wake the car over Bluetooth |

```bash
T=<token>
curl -H "Authorization: Bearer $T" http://<server-IP>:8090/api/state
curl -H "Authorization: Bearer $T" -X POST -d on http://<server-IP>:8090/api/vehicles/<VIN>/cmd/climate
curl -N -H "Authorization: Bearer $T" http://<server-IP>:8090/api/events      # live
```
