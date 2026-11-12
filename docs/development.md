<sub>[Tesla Bridge](../README.md) · [Guide](README.md) · Reference · Development</sub>

# Development

## Testing locally without a car

Only needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
cd src/TeslaBridge
dotnet run
```

Open **http://localhost:8080** and press **Send testdata** (send test data). The local test profile (`appsettings.Development.json`) starts the **embedded broker** (like way B) and a **simulator** that sends fake telemetry through MQTT exactly like fleet-telemetry. Commands fail gracefully with "not logged in to Tesla".

You can also point a test HA with the integration at `http://<your machine>:8080`.

**Bluetooth without an ESP32:** `python3 tools/fake_esp32.py` starts a pretend ESP32 on port 8099 that speaks ESPHome's web-server API. Set `http://localhost:8099` under Settings → Bluetooth. Commands then go "over Bluetooth" and change its state, and typing `away` or `home` (+ Enter) moves the car out of or into range.

> If something already uses port 1883, run `Bridge__Mqtt__Port=1884 dotnet run`.

## Project layout

```
tesla-bridge/
├── install.sh                      guided setup (curl … | bash)
├── docker-compose.yml              all the server needs (+ .env)
├── docker-compose.build.yml        build the bridge locally instead of pulling from ghcr.io
├── Dockerfile                      amd64 + arm64 (cross-compiled)
├── .env.example                    everything specific to you: domain, app, VIN, MQTT/embedded broker
├── hacs.json                       makes the repo installable from HACS
├── esphome/                        tesla-ble.yaml + secrets.yaml.example for the optional Bluetooth ESP32
├── tools/fake_esp32.py             a pretend ESP32 for trying Bluetooth without hardware
├── custom_components/tesla_bridge/ the HA integration (way B)
│   ├── __init__.py                 hub: fetches entities and state, listens to live events
│   ├── entity.py                   common base: renders the same templates as MQTT
│   ├── config_flow.py              URL + token, reauth
│   └── sensor.py, climate.py …     one file per platform
├── config/bridge/                  baked into the image (/app/defaults)
│   ├── telemetry-fields.json       fields + intervals sent to the car
│   └── vehicle-trust-ca.pem        root certificates the car trusts
├── src/TeslaBridge/
│   ├── Program.cs                  DI + CLI (init, auth, push-config, telemetry-status/-errors)
│   ├── Setup/Bootstrap.cs          keys, internal TLS, fleet-telemetry's config (tesla-init)
│   ├── Telemetry/                  SignalMapper (raw → normalized), ingest, snapshot, config push
│   ├── State/                      VehicleStateStore (confirmed vs. expected), persistence
│   ├── Commands/                   commands, parser, CommandService (optimistic + wake + retry)
│   ├── Ble/                        ESP32 over ESPHome's web server: status, state, BleTransport (tried before the Fleet API)
│   ├── Publishing/                 HomeAssistantDiscovery (the entities – used by BOTH ways), StatePublisher
│   ├── Mqtt/                       connection with reconnect and Last Will, embedded broker
│   ├── Settings/                   settings page: stored settings + description of every telemetry field
│   ├── Web/                        HTTP API, live events (SSE), 3D model upload
│   ├── wwwroot/                    dashboard: index.html, car3d.js (3D view), models/ (bundled Model Y, CC BY-NC), vendor/three (MIT)
│   └── Auth/, Usage/               Tesla login and usage tracking
├── tests/TeslaBridge.Tests/        .NET tests
└── tests/ha/                       the integration against a running bridge (pytest-homeassistant-custom-component)
```

**Adding or changing an entity:** do it in `Publishing/HomeAssistantDiscovery.cs`. Both MQTT discovery and `/api/entities` are built from it, so the integration picks up the change automatically.

**Tests:**
```bash
dotnet test                                              # .NET
(cd src/TeslaBridge && dotnet run &)                     # bridge with simulator on :8080
pip install pytest-homeassistant-custom-component
python -m pytest tests/ha -v                             # the integration against the bridge
```

**Releases:** pushing to `main` → GitHub Actions runs both test suites and builds `ghcr.io/mfoldager17/tesla-bridge:latest` (amd64 + arm64). A tag like `v1.2.0` also produces `:v1.2.0` and `:v1.2`.

**BLE later:** create a `BleTransport : ICommandTransport` with `Priority = 0` and register it in `Program.cs`. `CommandRouter` tries BLE first and falls back to the Fleet API.
