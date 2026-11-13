<sub>[Tesla Bridge](../README.md) · [Guide](README.md) · Reference · HTTP API</sub>

# HTTP API

This is the API the integration and the dashboard use. It runs on port 8090 in both ways, so ESP32 panels, Node-RED etc. can use it without MQTT too.

**Token:** every endpoint except `/api/info` and `/api/session` needs `Authorization: Bearer <token>` – `Bridge__Web__Token` from `.env`, or the one the bridge made in `data/web-token` (see [Security](security.md)). The dashboard signs in once with `POST /api/session` and then uses a session cookie. State-changing requests from another web site are refused (`403`).

**Errors** are [problem details](https://www.rfc-editor.org/rfc/rfc9457) (`application/problem+json`) with the message in both `detail` and `error`: `401` no or wrong token, `403` refused (cross-site, map turned off), `404` unknown VIN or path, `409` not signed in with Tesla yet or the car said no, `502` Tesla or the ESP32 failed, `504` timeout.

| Endpoint | |
|---|---|
| `GET /api/info` | version and whether a token is required (no token needed). With the token also: start time, cars, MQTT status, telemetry host |
| `POST /api/session` · `DELETE /api/session` | sign the dashboard in (`{"token": "…"}` → session cookie) or out |
| `GET /api/update` · `POST /api/update/check` | the newest release and whether it's newer than this one; check GitHub again now |
| `GET /api/entities` | all HA entities: the same definitions as MQTT discovery, with `state:<field>` / `cmd:<name>` instead of topics |
| `GET /api/state` | all fields per car with value, timestamp and source (including `*_motion`) |
| `GET /api/events` | Server-Sent Events, live: `field` (a value changed), `command` (command status), `raw` (telemetry as the car sent it), `connectivity`, `telemetry_error` |
| `GET /api/events/recent` | the last events, newest first (what the dashboard shows before the live stream starts) |
| `POST /api/vehicles/<VIN>/cmd/<name>` | a command. Body = the same payload as on MQTT (empty body = `PRESS`). Answers `202` straight away; with `?wait=true` it answers when the car has done it (woken first if needed): `200`, or `409`/`502` with `{"error": …}` |
| `GET /api/usage` | Fleet API usage |
| `GET /api/vehicles/<VIN>/telemetry-config` · `/telemetry-errors` | the telemetry configuration on the car (synced? which host?), and errors the car reports about the connection |
| `POST /api/vehicles/<VIN>/telemetry-config` | send the telemetry configuration to the car now |
| `GET /api/setup` | the setup steps: key, Tesla app, partner registration, sign-in, pairing and telemetry per car. `?check=true` asks Tesla again now |
| `POST /api/setup/register` · `GET /api/setup/pair-qr.svg` | register the partner domain with Tesla; the pairing link as a QR code |
| `GET /api/auth/start` | starts "Sign in with Tesla" (a redirect to Tesla) |
| `PUT /api/mcp-settings` | MCP on/off and whether commands that open the car are allowed: `{"enabled": true, "allow_security_commands": false}` |
| `POST /api/mcp-token` · `DELETE /api/mcp-token` | make a new MCP token (returned once) or revoke it |
| `POST /mcp` | the [MCP](mcp.md) server (Streamable HTTP) – its own token, not the dashboard's; 404 while turned off |
| `GET /api/geocode?lat=…&lon=…&lang=da` | the street address for a position (OpenStreetMap, cached). `403` when the map is turned off |
| `GET /api/settings` | telemetry fields with intervals, on/off and this month's signal count, plus the snapshot interval |
| `PUT /api/settings` | change them: `{"fields": {"Location": {"enabled": true, "interval_seconds": 10}}, "snapshot_interval_minutes": 60}` – saved and sent to the car. Also `car_view`, `climate_for_seat_heat`, `map_enabled` |
| `POST /api/settings/reset` | back to the defaults |
| `GET /api/car-model/info` | the chosen car view and which models exist |
| `GET /api/car-model` · `/api/car-model/builtin` | your uploaded model, or the bundled Model Y |
| `POST /api/car-model` · `DELETE /api/car-model` | upload (body = the .glb) or remove the uploaded model |
| `GET /api/ble` | per car: the [ESP32](ble.md) address, whether it's reachable, whether the car is in Bluetooth range, signal, the car's Bluetooth name |
| `PUT /api/ble/<VIN>` · `DELETE /api/ble/<VIN>` | set the ESP32: `{"url": "http://192.168.1.50", "username": "bridge", "password": "…"}` (password left out = unchanged; only private addresses), or forget the dashboard setting |
| `POST /api/ble/<VIN>/pair` · `/wake` | press *Pair BLE key* (then tap the key card in the car) or wake the car over Bluetooth |

```bash
T=<token>
curl -H "Authorization: Bearer $T" http://<server-IP>:8090/api/state
curl -H "Authorization: Bearer $T" -X POST -d on http://<server-IP>:8090/api/vehicles/<VIN>/cmd/climate
curl -N -H "Authorization: Bearer $T" http://<server-IP>:8090/api/events      # live
```
