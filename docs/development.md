<sub>[Tesla Bridge](../README.md) · [Guide](README.md) · Reference · Development</sub>

# Development

## Testing locally without a car

Only needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
cd src/TeslaBridge
dotnet run
```

Open **http://localhost:8080** and press **Send test data**. The local test profile (`appsettings.Development.json`) starts the **embedded broker** (like way B), a **simulator** that sends made-up telemetry through MQTT exactly like fleet-telemetry (every second press the car drives around Aarhus), and a pretend car that answers commands. It also runs **without a token** (`AllowAnonymous`), so you don't have to sign in; set `Bridge__Web__AllowAnonymous=false Bridge__Web__Token=…` to try the sign-in.

You can also point a test HA with the [integration](https://github.com/Mfoldager17/ha-tesla-bridge) at `http://<your machine>:8080`.

**Bluetooth without an ESP32:** `python3 tools/fake_esp32.py` starts a pretend ESP32 on port 8099 that speaks ESPHome's web-server API. Set `http://localhost:8099` under Settings → Bluetooth. Commands then go "over Bluetooth" and change its state, and typing `away` or `home` (+ Enter) moves the car out of or into range.

> If something already uses port 1883, run `Bridge__Mqtt__Port=1884 dotnet run`.

## The dashboard (web/)

The dashboard is a **SolidJS + Vite + TypeScript** app in `web/`. It is built into the bridge's `wwwroot` (`index.html` and `assets/`, never committed) and served on port 8090 like the API.

```bash
cd web
npm ci
npm run dev        # http://localhost:5173 – hot reload, /api proxied to a bridge on localhost:8080
                   # (another bridge: BRIDGE_URL=http://192.168.1.20:8090 npm run dev)
npm run check      # TypeScript
npm test           # Vitest
npm run build      # → src/TeslaBridge/wwwroot, served by `dotnet run` too
```

`dotnet run` on its own serves the API but no dashboard until you have run `npm run build` once (or use `npm run dev`). The Docker image builds it in its own stage, and CI runs check, test and build.

| | |
|---|---|
| `src/api/` | types for the bridge's API, the client (session sign-in, 401 → login) and `send()` for commands |
| `src/state/` | live state fed by `/api/events` (Server-Sent Events), the shown car, settings, setup, car model, update check |
| `src/views/` | one file per page – `settings/` holds the settings page's panels |
| `src/components/` | the menu, tiles, the car (3D via `lib/car3d.ts` or the drawing), the map card, command log, toast |
| `src/i18n/` | English and Danish (`en.json`, `da.json` – same keys, tested) |
| `src/styles/app.css` | the styles |
| `src/lib/` | the 3D car (three.js) and the map (Leaflet) – each loaded as its own chunk only when shown – plus formatting helpers |
| `scripts/` | build helpers: clear the previous build, write `THIRD-PARTY-LICENSES.txt` |

TypeScript runs in strict mode and is the linter for now: typescript-eslint doesn't support TypeScript 7 yet.

## Commits and releases

Releases are made by [release-please](https://github.com/googleapis/release-please) from the commit messages, so every commit to `main` starts with a type ([Conventional Commits](https://www.conventionalcommits.org)):

| Commit message | Goes in the changelog as | Next version |
|---|---|---|
| `fix: seat heat turns the climate on first` | Bug fixes | 1.2.0 → 1.2.1 |
| `feat: Bluetooth through an ESP32` | Features | 1.2.0 → 1.3.0 |
| `feat!: new .env format` (or a `BREAKING CHANGE:` footer) | Breaking change | 1.2.0 → 2.0.0 |
| `docs: …` | Documentation | none on its own |
| `chore:`, `test:`, `ci:`, `refactor:` | not shown | none |

A scope is optional: `fix(mqtt): …`, `feat(dashboard): …`. One commit can carry several changes. Put the extra ones as their own `feat: …` / `fix: …` lines at the end of the message body.

How a release happens:

1. Every push to `main` builds the image `edge` (`.github/workflows/build.yml`).
2. release-please keeps one PR, *chore(main): release x.y.z*, up to date with the next version number, `CHANGELOG.md` and `<Version>` in the project file (`.github/workflows/release.yml`).
3. **Merging that PR is the release:** tag `vX.Y.Z`, a GitHub Release (the bridge's update entity in Home Assistant shows it), and the images `vX.Y.Z`, `vX.Y`, `vX` and `latest` (`.github/workflows/image.yml`).

Don't change the version number by hand. To force one, add `Release-As: 2.0.0` as a footer in a commit message.

The running version is in `GET /api/info` (`version`, `version_display`, `commit`, `channel`). The Home Assistant integration lives in its own repository, [ha-tesla-bridge](https://github.com/Mfoldager17/ha-tesla-bridge), with its own versions. It has a minimum bridge version (`MIN_BRIDGE_VERSION`) and its CI tests it against this repository's `main` every week, so keep `/api/entities`, `/api/state` and `/api/events` backwards compatible: add, don't rename or remove.

## Project layout

```
tesla-bridge/
├── install.sh                      guided setup (curl … | bash)
├── docker-compose.yml              all the server needs (+ .env)
├── docker-compose.build.yml        build the bridge locally instead of pulling from ghcr.io
├── Dockerfile                      amd64 + arm64 (cross-compiled)
├── .env.example                    everything specific to you: domain, app, VIN, MQTT/embedded broker
├── esphome/                        tesla-ble.yaml + secrets.yaml.example for the optional Bluetooth ESP32
├── tools/fake_esp32.py             a pretend ESP32 for trying Bluetooth without hardware
├── config/bridge/                  baked into the image (/app/defaults)
│   ├── telemetry-fields.json       fields + intervals sent to the car
│   └── vehicle-trust-ca.pem        root certificates the car trusts
├── web/                            the dashboard (SolidJS + Vite + TypeScript), built into src/TeslaBridge/wwwroot
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
│   ├── Web/                        HTTP API, security (token, session, cross-site, headers), live events (SSE), 3D model upload
│   ├── Storage/AtomicFile.cs       every file the bridge writes: temp file + flush + rename, 0600 for secrets
│   ├── Map/                        street addresses (Nominatim) for the dashboard's map
│   ├── wwwroot/                    models/ (bundled Model Y, CC BY-NC); the built dashboard lands here
│   └── Auth/, Usage/               Tesla login and usage tracking
└── tests/TeslaBridge.Tests/        .NET tests
```

**Adding or changing an entity:** do it in `Publishing/HomeAssistantDiscovery.cs`. Both MQTT discovery and `/api/entities` are built from it, so the integration picks up the change automatically – no release of it needed.

**Tests and checks** (CI runs all of them on every pull request):
```bash
dotnet format --verify-no-changes                        # C# formatting (.editorconfig)
dotnet test                                              # .NET – analyzers are on and warnings fail the build (Directory.Build.props)
ruff check .                                             # Python (tools/)
shellcheck install.sh
```

**Adding a way to the car:** implement `ICommandTransport` (see `Ble/BleTransport.cs`) and register it in `Program.cs`. `CommandRouter` tries transports by `Priority` (Bluetooth 0, simulator 50, Fleet API 100) and falls back to the next one.
