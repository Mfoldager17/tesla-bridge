# Tesla Bridge

A self-hosted replacement for Teslemetry. Your car streams directly to your own server, and you control it through Tesla's official Fleet API – no middleman, no subscription. Fleet API usage for one car stays within Tesla's free monthly credit ($10), and the bridge tracks it for you.

- **Home Assistant your way:** through your MQTT broker, or a real integration without one. Same entities either way, named like Teslemetry's.
- **Feels instant:** commands show up straight away, locks and covers show *locking*/*opening*, a sleeping car is woken and the command resent.
- **Its own dashboard:** the car in 3D, controls, live data, usage and settings – also without Home Assistant.
- **Your choice how often:** pick per value how often the car sends it, with a live cost estimate.

<table>
<tr>
<td width="66%"><img src="docs/screenshots/car.png" alt="The car page: a 3D Model Y with an open rear window marked, 68 % battery charging at 10.8 kW towards an 80 % limit, quick actions and status"></td>
<td><img src="docs/screenshots/phone-car.png" alt="The car page on a phone in light mode"></td>
</tr>
<tr>
<td><b>Car</b> – the car in 3D with open doors, windows, frunk, trunk and charge port live; battery, range and charging; quick actions.</td>
<td><b>On a phone</b></td>
</tr>
<tr>
<td><img src="docs/screenshots/controls.png" alt="Controls: climate with temperature, Keep/Dog/Camp and overheat protection; seat and steering wheel heat; charging limit and current; locks, sentry, guest and valet mode; frunk, trunk, windows and HomeLink"></td>
<td><img src="docs/screenshots/phone-controls.png" alt="Controls on a phone"></td>
</tr>
<tr>
<td><b>Controls</b> – everything Home Assistant can do, with the car's current state.</td>
<td></td>
</tr>
<tr>
<td><img src="docs/screenshots/settings.png" alt="Settings: quick choices Save money, Recommended and As live as possible, and a list of telemetry fields with their interval, this month's signal count and an on/off switch"></td>
<td><img src="docs/screenshots/usage.png" alt="Usage: money spent this month, the projection to the end of the month and what is left of the free credit"></td>
</tr>
<tr>
<td><b>Settings</b> – how often the car sends each value, with this month's signal count and a cost estimate.</td>
<td><b>Usage</b> – Fleet API cost this month and the projection.</td>
</tr>
</table>

<sub>Screenshots use test data from the built-in simulator. 3D model: “2025 Tesla Model Y” by BloxBloger, CC BY-NC 4.0, modified – see <a href="docs/5-the-dashboard.md#credits">credits</a>.</sub>

## Get started

```
1. Before you start  →  2. Install  →  3. Connect the car  →  4. Home Assistant  →  5. The dashboard
                        script or by hand                     MQTT or integration
```

**[Start with step 1 →](docs/1-before-you-start.md)**

Already know what you're doing? On the server:

```bash
curl -fsSL https://raw.githubusercontent.com/Mfoldager17/tesla-bridge/main/install.sh | bash
```

## Reference

| | |
|---|---|
| [Entities](docs/entities.md) | Everything you get in Home Assistant |
| [Switching](docs/switching.md) | From MQTT to the integration and back |
| [Updating](docs/updating.md) | New versions and pinning one |
| [Troubleshooting](docs/troubleshooting.md) | Symptoms and what to check |
| [MQTT API](docs/mqtt-api.md) · [HTTP API](docs/http-api.md) | For ESP32 panels, Node-RED and scripts |
| [Development](docs/development.md) | Run it locally with the simulator, project layout, tests |

Tesla and Model Y are trademarks of Tesla, Inc. This project is not affiliated with Tesla.
