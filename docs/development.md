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

## The dashboard (web/)

The dashboard is being rewritten in **SolidJS + Vite + TypeScript** in `web/` (phase 2). Until it is done, the new one runs next to the old one: `http://<server>:8090/next/` is the new one, `http://<server>:8090/` still the old (`src/TeslaBridge/wwwroot/index.html`).

```bash
cd web
npm ci
npm run dev        # http://localhost:5173/next/ – hot reload, /api proxied to a bridge on localhost:8080
                   # (another bridge: BRIDGE_URL=http://192.168.1.20:8090 npm run dev)
npm run check      # TypeScript
npm test           # Vitest
npm run build      # → src/TeslaBridge/wwwroot/next, served by `dotnet run` too
```

The Docker image builds it in its own stage, and CI runs check, test and build. The build output is never committed.

| | |
|---|---|
| `src/api/` | types for the bridge's API and the client (token, 401 → login) |
| `src/state/live.ts` | all cars' fields, the command log and raw events, fed by `/api/events` (Server-Sent Events) |
| `src/i18n/` | English and Danish (`en.json`, `da.json` – same keys, tested) |
| `src/components/`, `src/views/` | the frame and the pages |
| `src/styles/app.css` | the styles, ported from the old dashboard |

## Commits and releases

Releases are made by [release-please](https://github.com/googleapis/release-please) from the commit messages, so every commit to `main` starts with a type ([Conventional Commits](https://www.conventionalcommits.org)):

| Commit message | Goes in the changelog as | Next version |
|---|---|---|
| `fix: seat heat turns the climate on first` | Bug fixes | 1.2.0 → 1.2.1 |
| `feat: Bluetooth through an ESP32` | Features | 1.2.0 → 1.3.0 |
| `feat!: new .env format` (or a `BREAKING CHANGE:` footer) | Breaking change | 1.2.0 → 2.0.0 |
| `docs: …` | Documentation | none on its own |
| `chore:`, `test:`, `ci:`, `refactor:` | not shown | none |

A scope is optional: `fix(integration): …`, `feat(dashboard): …`. One commit can carry several changes. Put the extra ones as their own `feat: …` / `fix: …` lines at the end of the message body.

How a release happens:

1. Every push to `main` builds the image `edge` (`.github/workflows/build.yml`).
2. release-please keeps one PR, *chore(main): release x.y.z*, up to date with the next version number, `CHANGELOG.md`, `manifest.json` and `<Version>` in the project file (`.github/workflows/release.yml`).
3. **Merging that PR is the release:** tag `vX.Y.Z`, a GitHub Release (HACS offers it), and the images `vX.Y.Z`, `vX.Y`, `vX` and `latest` (`.github/workflows/image.yml`).

Don't change the version number by hand. To force one, add `Release-As: 2.0.0` as a footer in a commit message.

The running version is in `GET /api/info` (`version`, `version_display`, `commit`, `channel`). The integration's minimum bridge version is `MIN_BRIDGE_VERSION` in `custom_components/tesla_bridge/const.py`. Raise it when the integration starts using something a newer bridge added.

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
