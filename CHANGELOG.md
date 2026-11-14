# Changelog

## [2.0.0](https://github.com/Mfoldager17/tesla-bridge/compare/v1.1.0...v2.0.0) (2026-11-14)


### ⚠ BREAKING CHANGES

* the dashboard and API always need a token (made on first start when Bridge__Web__Token is empty), so the Home Assistant integration must be given it once. The embedded MQTT broker no longer accepts anonymous logins, and the ESP32 firmware requires a web password. Fetch the new docker-compose.yml for the hardened containers. See docs/updating.md.

### Features

* **dashboard:** a new dashboard in SolidJS + TypeScript replaces the old one: Car, Controls, Data, Usage, Settings and Setup pages ([18c3e51](https://github.com/Mfoldager17/tesla-bridge/commit/18c3e51473bf10cc5927c2d59931c82f3c403985))
* **dashboard:** map and street address of the car ([6c61cad](https://github.com/Mfoldager17/tesla-bridge/commit/6c61cadee674cdd28da806ddab67012308cf7d33))
* **mcp:** MCP server for AI assistants, off by default and with its own token ([691845f](https://github.com/Mfoldager17/tesla-bridge/commit/691845f6e09cdad15c799ecb659c8c756dd2e348))
* **mcp:** choose which commands an assistant may send ([237a04d](https://github.com/Mfoldager17/tesla-bridge/commit/237a04dea769a8c7f03c34a8b77c38efd7fb1261))
* **security:** token always on, MQTT and ESP32 logins, hardened containers, review fixes ([53cd888](https://github.com/Mfoldager17/tesla-bridge/commit/53cd8889bcdba01e26bf6c29243bbc5074713e8d))
* **security:** warn when the MQTT broker lets anyone in ([f488ab2](https://github.com/Mfoldager17/tesla-bridge/commit/f488ab2bd6c117375d832b3260847868e9abf61d))


### Bug fixes

* **deps:** tesla/fleet-telemetry v0.9.5 → v0.10.0 ([868139d](https://github.com/Mfoldager17/tesla-bridge/commit/868139df227708133973bc278f2c7b477c267c41))
* **deps:** three.js 0.169 → 0.187 for the 3D car ([6f7b6e8](https://github.com/Mfoldager17/tesla-bridge/commit/6f7b6e8c5be396587ea6dbce7391f1bee581b150))
* **security:** parse Tesla's token answer with JsonElement.Parse (CA2026) ([b9f3a3d](https://github.com/Mfoldager17/tesla-bridge/commit/b9f3a3d75c0ca672c5179c57947697504e85c7c2))


### Documentation

* how to update to 2.0 ([#23](https://github.com/Mfoldager17/tesla-bridge/issues/23)) ([83fc9f7](https://github.com/Mfoldager17/tesla-bridge/commit/83fc9f70a978a744ef52d86873edc6873e1d8b4c))
* new screenshots with the map, and Dependabot for every dependency ([158fd9d](https://github.com/Mfoldager17/tesla-bridge/commit/158fd9d379ce9a78249784f5d61569cd4226d294))

## [1.1.0](https://github.com/Mfoldager17/tesla-bridge/compare/v1.0.0...v1.1.0) (2026-11-12)


### Features

* Bluetooth through an ESP32 – free commands and wake-ups while the car is home ([0f5b0eb](https://github.com/Mfoldager17/tesla-bridge/commit/0f5b0eb4d4cf1b8ea911d2da9048847404a1a76f))
* **dashboard:** new dashboard with 3D car, controls, data, usage, settings and setup ([0f5b0eb](https://github.com/Mfoldager17/tesla-bridge/commit/0f5b0eb4d4cf1b8ea911d2da9048847404a1a76f))
* **dashboard:** telemetry intervals per field with a live cost estimate ([0f5b0eb](https://github.com/Mfoldager17/tesla-bridge/commit/0f5b0eb4d4cf1b8ea911d2da9048847404a1a76f))
* entities show when they wait for the car (pending / waiting_for, wake-up icon) ([0f5b0eb](https://github.com/Mfoldager17/tesla-bridge/commit/0f5b0eb4d4cf1b8ea911d2da9048847404a1a76f))
* guided setup script (install.sh) and automatic partner domain registration ([0f5b0eb](https://github.com/Mfoldager17/tesla-bridge/commit/0f5b0eb4d4cf1b8ea911d2da9048847404a1a76f))
* Home Assistant integration without MQTT, with an embedded broker as alternative ([0f5b0eb](https://github.com/Mfoldager17/tesla-bridge/commit/0f5b0eb4d4cf1b8ea911d2da9048847404a1a76f))
* **integration:** actions wait for the car like Teslemetry and show the car's error ([0f5b0eb](https://github.com/Mfoldager17/tesla-bridge/commit/0f5b0eb4d4cf1b8ea911d2da9048847404a1a76f))
* interactive setup guide website and step-by-step docs ([0f5b0eb](https://github.com/Mfoldager17/tesla-bridge/commit/0f5b0eb4d4cf1b8ea911d2da9048847404a1a76f))
* reverse proxy or tunnel (e.g. Cloudflare Tunnel), any certificate folder, Nginx Proxy Manager not required ([0f5b0eb](https://github.com/Mfoldager17/tesla-bridge/commit/0f5b0eb4d4cf1b8ea911d2da9048847404a1a76f))
* seat and steering-wheel heat turn the climate on first (optional) ([0f5b0eb](https://github.com/Mfoldager17/tesla-bridge/commit/0f5b0eb4d4cf1b8ea911d2da9048847404a1a76f))
* versioned releases with a changelog, release channels and update notices ([0f5b0eb](https://github.com/Mfoldager17/tesla-bridge/commit/0f5b0eb4d4cf1b8ea911d2da9048847404a1a76f))


### Bug fixes

* a renewed Tesla token is kept when the disk is full; container logs are capped ([0f5b0eb](https://github.com/Mfoldager17/tesla-bridge/commit/0f5b0eb4d4cf1b8ea911d2da9048847404a1a76f))
* parked car – invalid gear becomes P, invalid speed 0, invalid range keeps the last value ([0f5b0eb](https://github.com/Mfoldager17/tesla-bridge/commit/0f5b0eb4d4cf1b8ea911d2da9048847404a1a76f))

## Changelog

All notable changes to Tesla Bridge. Written by release-please from the commit messages – see [Versions and updating](docs/updating.md).
