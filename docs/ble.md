<sub>[Tesla Bridge](../README.md) · [Guide](README.md) · Optional · Bluetooth (ESP32)</sub>

# Bluetooth with an ESP32 – free commands at home

**Optional.** You can add an ESP32 (an inexpensive Wi-Fi/Bluetooth board) near where the car parks: in the carport, the garage or a window facing the driveway. While the car is within Bluetooth range, the bridge sends commands and wake-ups to the ESP32 instead of the Fleet API:

| | Through the Fleet API | Through the ESP32 (Bluetooth) |
|---|---|---|
| Cost | commands $1 / 1,000, wake-ups $1 / 50 | **free** |
| Needs the internet | yes, both your server and the car | no – the car can be in an underground garage |
| Speed | 1–3 s, longer when the car sleeps | usually under a second; the ESP32 wakes the car by itself |
| Works | everywhere | within ~10–30 m of the ESP32 |

Nothing else changes. When the car drives away, the bridge simply uses the Fleet API again, and the streamed data still comes through fleet-telemetry as before. Home Assistant, the dashboard and your automations don't notice the difference, apart from commands working faster and for free at home.

While the car is in range, the ESP32 also reads the battery, charging, locks, doors and temperatures over Bluetooth, which keeps those values fresh at no cost.

## What goes over Bluetooth

| Over Bluetooth when the car is home | Always the Fleet API |
|---|---|
| Climate on/off, temperature, defrost, steering wheel heat | Seat heaters, climate keeper (dog/camp) |
| Lock / unlock | Guest mode, valet mode |
| Start/stop charging, charge limit, charging current | Location-based things: HomeLink |
| Charge port open/close, frunk, trunk, vent/close windows | Remote start, boombox |
| Sentry mode, cabin overheat protection | |
| Flash lights, honk, **wake up** | |

Even for a command that only the Fleet API can do, a sleeping car is **woken over Bluetooth** first, and that wake-up is the expensive part ($1 per 50).

The dashboard's **Usage** page shows how many commands went over Bluetooth this month and what that saved. The *Command status* entity in Home Assistant has the route of the last command in its `transport` attribute (`ble` or `fleet-api`). A new diagnostic entity, `binary_sensor.<car>_bluetooth`, is on while the car is in range.

## What you need

- **An ESP32 board.** A plain ESP32 dev board (ESP32-WROOM, "ESP32 DevKit") works, and so do the M5Stack Atom S3, Atom S3 Lite and Nano C6. *Not* the ESP32-S2, which has no Bluetooth. Power it from a USB charger.
- **Wi-Fi at the spot**, and the board within Bluetooth range of where the car parks. Closer is better; it works through a wall or a garage door.
- **ESPHome** to flash it: the *ESPHome Device Builder* add-on in Home Assistant (easiest), the `esphome` command line (`pip install esphome`), or the browser at [web.esphome.io](https://web.esphome.io) for the very first flash.
- Your car's **VIN** and its **Bluetooth MAC address** (step 1).

The firmware is the open-source [ESPHome Tesla BLE](https://github.com/PedroKTFC/esphome-tesla-ble) component by PedroKTFC (AGPL-3.0). This repository only adds a ready-made configuration for it, [`esphome/tesla-ble.yaml`](../esphome/tesla-ble.yaml), which turns on what the bridge needs.

## Step 1 – Find the car's Bluetooth MAC address

The car advertises itself over Bluetooth with a name built from the VIN: **`S` + 16 characters + `C`**. The dashboard shows your car's exact name under **Settings → Bluetooth → How do I get an ESP32 running?**

1. Stand next to the car with your phone and open a Bluetooth scanner app, such as *nRF Connect* (Android/iOS) or *BLE Scanner*.
2. Find the device with exactly that name.
3. Note its MAC address, e.g. `A0:B1:C2:D3:E4:F5`.

<details><summary>No phone app? Let the ESP32 find it</summary>

Flash the ESP32 once with the `packages/listener.yml` line uncommented in `tesla-ble.yaml`, and with any MAC address in `secrets.yaml`. Then watch the logs (`esphome logs tesla-ble.yaml`, or *Logs* in the Device Builder) near the car:

```
[I][tesla_ble_listener]: Found Tesla vehicle | Name: S1a87a5a75f3df858C | MAC: A0:B1:C2:D3:E4:F5
```

Put the MAC in `secrets.yaml`, comment the line out again and flash once more.
</details>

## Step 2 – Flash the ESP32

Get the two files from this repository into your ESPHome folder (in the Device Builder: *New device → Skip*, then paste the YAML):

```bash
mkdir tesla-ble && cd tesla-ble
curl -fsSLO https://raw.githubusercontent.com/Mfoldager17/tesla-bridge/main/esphome/tesla-ble.yaml
curl -fsSL  https://raw.githubusercontent.com/Mfoldager17/tesla-bridge/main/esphome/secrets.yaml.example -o secrets.yaml
nano secrets.yaml          # Wi-Fi, VIN, MAC address, an API key (openssl rand -base64 32)
```

Connect the ESP32 with USB and flash it:

```bash
pip install esphome
esphome run tesla-ble.yaml            # the first build takes a while; later updates go over Wi-Fi
```

When it's running, `http://tesla-ble.local` (or `http://<the ESP32's IP>`) opens the ESP32's own little web page with all its values. Give it a fixed IP address in your router, so the bridge can always find it.

**Already running ESPHome Tesla BLE in Home Assistant?** Then you don't need a new ESP32. Add these lines to your existing YAML and flash it again:

```yaml
web_server:
  port: 80
  version: 3
api:
  reboot_timeout: 0s
```

Keep the English entity names: the bridge finds the entities by name, so leave out `language_xx.yml` packages. Both can run at the same time: Home Assistant via the ESPHome integration and the bridge via the web server.

## Step 3 – Connect it to the bridge

On the dashboard: **Settings → Bluetooth (ESP32)**. Type the address, e.g. `http://192.168.1.50`, and press **Save**.

Within a few seconds the status shows one of these:

| Status | Meaning |
|---|---|
| 🟢 *Car in range – commands go over Bluetooth* | Everything works |
| 🟡 *ESP32 connected – the car isn't nearby* | Normal when the car is away |
| 🔴 *Can't reach the ESP32* | Wrong address, ESP32 off, or a different network. The reason is shown underneath |
| 🟡 *…doesn't have the Tesla BLE entities* | It is an ESPHome device, but not flashed with `tesla-ble.yaml` |

You can also put the address in `.env` instead (`Bridge__Vehicles__0__Ble__Url=http://192.168.1.50`), and the setup script asks for it too. If both are set, the dashboard's setting wins. With `auth:` in `web_server`, also fill in the username and password.

## Step 4 – Pair the ESP32 with the car (once)

The ESP32 is a key of its own, just like your phone. It must be approved in the car once:

1. Sit in the car with your **key card**.
2. On the dashboard, press **Pair key** (Settings → Bluetooth). You can also press *Pair BLE key* on the ESP32's own web page.
3. Tap the key card on the console between the front seats, and confirm on the car's screen.
4. Under *Controls → Locks* on the car's screen a new key appears, typically called "Unknown device". Rename it to e.g. "ESP32 carport", so you know what it is.

Then press **Test: wake the car**. If the car wakes, you're done.

## Good to know

- **Out of range** is detected within ~30 seconds. A command sent in that window falls back to the Fleet API by itself.
- **Errors fall back too.** If the ESP32 doesn't answer, the bridge sends the command through the Fleet API instead, so a broken ESP32 never stops commands from working.
- **Removing the ESP32:** clear the address on the dashboard (or in `.env`), and delete its key in the car under *Controls → Locks*.
- **Several cars:** one ESP32 per car (`Bridge__Vehicles__1__Ble__Url` …). Each car has its own row on the dashboard.
- **Updating the firmware:** `esphome run tesla-ble.yaml` again (over Wi-Fi). The configuration follows the component's newest version.
- **Without hardware:** `python3 tools/fake_esp32.py` pretends to be an ESP32, for trying it out. See [Development](development.md).

| Problem | Check |
|---|---|
| *Can't reach the ESP32* | Open `http://<ESP32>/` in a browser on the server's network. Docker on the server must be able to reach the ESP32's IP (same LAN). `.local` names often don't work inside Docker, so use the IP |
| The car is never "in range" | Look at *BLE Status* on the ESP32's web page. Move it closer, check the MAC address in `secrets.yaml` |
| Commands still use the Fleet API | Is the key paired (step 4)? Check the `transport` attribute of *Command status* in HA, or the command log on the dashboard's Data page: it says `ble` or `fleet-api` |
| The ESP32 restarts every 15 minutes | `api: reboot_timeout: 0s` is missing from its YAML |

---

**Back:** [The dashboard](5-the-dashboard.md) · [Guide](README.md)
