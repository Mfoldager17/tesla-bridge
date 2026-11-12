# Changelog

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
