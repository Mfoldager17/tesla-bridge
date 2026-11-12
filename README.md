# Tesla Bridge

A self-hosted replacement for Teslemetry. Your car streams directly to your own server, and you control it through Tesla's official Fleet API – no middleman, no subscription.

Data can reach Home Assistant in **two ways**. Pick the one that suits you:

| | **A) MQTT** | **B) HA integration** |
|---|---|---|
| For you if … | you already run an MQTT broker (Mosquitto, e.g. for Zigbee2MQTT) | you don't have or don't want a broker |
| Extra software | your broker | none |
| In Home Assistant | the MQTT integration (discovery, automatic) | **Tesla Bridge** from HACS |
| Other clients (ESP32, Node-RED, custom dashboards) | read and write MQTT topics | use the HTTP API |
| Entities | ~80 under one device per car | exactly the same names, types and entity ids |

> **About the examples in this guide**
> Every concrete value below is an **example** that you replace with your own:
>
> | Example | Replace with |
> |---|---|
> | `car.example.com` | your own (sub)domain. The name is up to you (`car.`, `ev.`, `tlm.` …) – it just must not contain "tesla" |
> | `Model Y` / `model_y_` | the name you give your car in `.env` (see below) |
> | `<server-IP>`, `192.168.0.x` | the IP address of your server and your MQTT broker |
> | `mosquitto` | the name of *your* Mosquitto container (`docker ps`) |
> | `~/docker-services/…` | wherever you keep your docker projects |
> | `npm-<nr>` | the certificate number in your Nginx Proxy Manager |
> | `<VIN>`, `<password 1>`, `<password 2>`, `<token>` | your VIN and your own passwords/token |
>
> Text in `<angle brackets>` must always be replaced – the brackets themselves are not included.

In HA, the car is called whatever you put in `Bridge__Vehicles__0__Name` in `.env` (e.g. "Model Y", "My Car" or "Elsa"), and that name becomes the prefix of every entity id: "Model Y" → `sensor.model_y_battery_level`, "My Car" → `sensor.my_car_battery_level`. In the examples here, the car is called "Model Y".

Both ways produce **identical entities** (`sensor.<car>_battery_level`, `climate.<car>_climate`, `lock.<car>_lock` …), because the integration fetches the same definitions from the bridge that MQTT discovery uses. You can switch later without touching dashboards or automations. The names follow Teslemetry, so moving from there only means changing the entity id prefix (`sensor.tesla_battery_level` → `sensor.model_y_battery_level`). To avoid even that, give the car the same name it has in Teslemetry.

The cost is Tesla's own Fleet API billing, which for one car stays within the free monthly credit ($10). The bridge tracks your usage for you.

---

## Contents

1. [How it fits together](#how-it-fits-together)
2. [Requirements](#requirements)
3. [Installation](#installation) (steps 1–8, with A and B along the way)
4. [Switching between MQTT and the integration](#switching-between-mqtt-and-the-integration)
5. [Entities in Home Assistant](#entities-in-home-assistant)
6. [Updating](#updating)
7. [Troubleshooting](#troubleshooting)
8. [The dashboard](#the-dashboard)
9. [Fields and intervals](#fields-and-intervals)
10. [MQTT API](#mqtt-api-way-a)
11. [HTTP API](#http-api-way-b-and-everything-else)
12. [Testing locally without a car](#testing-locally-without-a-car)
13. [Development](#development)

---

## How it fits together

**Way A – with your own broker**
```
Car ──mTLS:4443──▶ fleet-telemetry ──MQTT──▶ your broker ◀──MQTT──▶ tesla-bridge
                                                  │                     │
                                      HA (MQTT discovery),        tesla-proxy ──▶ Tesla Fleet API ──▶ Car
                                      ESP32, Node-RED …
```

**Way B – without a broker**
```
Car ──mTLS:4443──▶ fleet-telemetry ──MQTT──▶ tesla-bridge (embedded broker, internal to Docker only)
                                                  │
                               HTTP + live events (:8090)
                                                  │
                                 the "Tesla Bridge" HA integration
                                                  │  commands
                                                  ▼
                                    tesla-proxy ──▶ Tesla Fleet API ──▶ Car
```

| Container | From | Job |
|---|---|---|
| `fleet-telemetry` | Tesla (official) | Receives the car's stream (mTLS on port 4443) and puts raw fields on MQTT |
| `tesla-proxy` | Tesla `vehicle-command` | Signs commands with your app's key |
| `tesla-init` | this repo | Runs on every start and exits: creates keys and fleet-telemetry's config from `.env` |
| `tesla-bridge` | this repo (.NET 10) | Normalizes data, HA entities, commands, optimistic state, dashboard, usage |

**What the bridge fixes compared to the Tesla Fleet integration:**
- **No "off → on" flicker.** Press "climate on" and it shows "on" immediately. The car's stale snapshot right after waking does not overwrite it. If the car doesn't confirm within 45 s, it rolls back.
- **Transition states like Teslemetry.** Locks and covers show `locking`/`unlocking`/`opening`/`closing` while the car works.
- **Wakes the car itself.** If the car is asleep, it is woken and the command is resent as soon as it's online.
- **Never "unknown" after a restart.** Last known values are saved in `data/state.json`. When the car wakes, a snapshot is fetched (at most once an hour) for fields it hasn't streamed yet.
- **Runs without HA.** The bridge, dashboard, login and key pairing all work without Home Assistant.

---

## Requirements

For both ways:

- **A server with Docker**, e.g. a Raspberry Pi 4/5 (arm64) or an x86 machine. The image is built for both.
- **A (sub)domain** pointing to your public IP, e.g. `car.example.com` (example – the name is up to you).
  - Tesla does **not** allow the word "tesla" in the domain or the app name, so `tesla.example.com` will be rejected. Otherwise the subdomain can be anything.
  - On Cloudflare: **DNS only** (grey cloud), not proxied. The car must talk directly to your server.
- **A valid certificate** for the domain (Let's Encrypt, e.g. from Nginx Proxy Manager).
- **A reverse proxy** (NPM or similar) forwarding `https://<your domain>` to `<server-IP>:8091`. That port *only* answers with the public key and the login callback, never the dashboard.
- **Port forward 4443** (TCP) from your router to the server. The car sends its data here. NPM and your existing hosts on 443 are not affected.
  - Cloudflare Tunnel and ngrok (HTTP) do **not** work: they terminate TLS and thereby break the car's mTLS. If you need a tunnel, it must be raw TCP (e.g. Tailscale Funnel in TCP mode or a VPS with WireGuard).
- **A new app** on [developer.tesla.com](https://developer.tesla.com) (step 1).

Way A only:
- **An MQTT broker** that both the server and HA can reach. Your existing one works fine.

Way B only:
- **HACS** in Home Assistant (or copy the folder manually, see step 8B).

---

## Installation

### 1. Create the app at Tesla

On [developer.tesla.com](https://developer.tesla.com) → *Create application*:

| Field | Value |
|---|---|
| Name | your choice, just *without* "tesla", e.g. `My Car` |
| Allowed Origin | `https://<your domain>` (e.g. `https://car.example.com`) |
| Allowed Redirect URI | `https://<your domain>/auth/callback` |
| Scopes | Vehicle Information, Vehicle Location, Vehicle Commands, Vehicle Charging Management |

Note the **Client ID** and **Client Secret**. You do *not* create keys yourself: the bridge generates the key pair and serves the public key at `https://<your domain>/.well-known/appspecific/com.tesla.3p.public-key.pem`.

### 2. Domain, certificate and reverse proxy

1. Create a DNS record for your domain (e.g. `car.example.com`) → your public IP (DNS only).
2. In NPM: a new proxy host `<your domain>` → `http://<server-IP>:8091`, with a Let's Encrypt certificate and *Force SSL*.
3. Forward **TCP 4443** from your router to the server.

### 3. Download the compose file and create `.env`

The server **only** needs two files. No source code, no `git`. The bridge is pulled prebuilt from `ghcr.io`.

```bash
mkdir -p ~/docker-services/tesla-bridge && cd ~/docker-services/tesla-bridge
curl -fsSLO https://raw.githubusercontent.com/Mfoldager17/tesla-bridge/main/docker-compose.yml
curl -fsSL  https://raw.githubusercontent.com/Mfoldager17/tesla-bridge/main/.env.example -o .env
chmod 600 .env
nano .env
```

Fill in the common values (all values are examples):

```ini
TZ=Europe/Copenhagen
PUID=1000                                   # from `id`
PGID=1000
Bridge__Telemetry__Hostname=car.example.com # YOUR domain from step 2
Bridge__Vehicles__0__Vin=<VIN>              # 17 characters, in the Tesla app under Software
Bridge__Vehicles__0__Name=Model Y           # your choice → device "Model Y", entity ids model_y_*
Bridge__Tesla__ClientId=<Client ID from step 1>
Bridge__Tesla__ClientSecret=<Client Secret from step 1>
Bridge__Web__Token=<token>                  # long random string, e.g.: openssl rand -hex 24
```

Then fill in **either A or B**:

<table>
<tr><th>A) MQTT</th><th>B) Integration (no broker)</th></tr>
<tr><td>

```ini
Bridge__Mqtt__EmbeddedBroker=false
Bridge__Mqtt__Host=192.168.0.x   # YOUR broker's IP
Bridge__Mqtt__Port=1883
Bridge__Mqtt__Username=tesla-bridge
Bridge__Mqtt__Password=<password 1>
Bridge__Telemetry__MqttUsername=fleet-telemetry
Bridge__Telemetry__MqttPassword=<password 2>
Bridge__Mqtt__DiscoveryPrefix=homeassistant
```

Use the IP, **not** `localhost`. The bridge and fleet-telemetry run in containers and must be able to reach the broker.

</td><td>

```ini
Bridge__Mqtt__EmbeddedBroker=true
Bridge__Mqtt__DiscoveryPrefix=
```

The other MQTT fields can stay empty. The bridge starts a small broker of its own that only fleet-telemetry talks to over the docker network. It is not reachable from outside.

</td></tr>
</table>

### 4. MQTT users (A only)

The bridge and fleet-telemetry log in to your broker with a user each. The usernames `tesla-bridge` and `fleet-telemetry` are suggestions – use others if you like, as long as they match here and in `.env`. With Mosquitto in a container (replace `mosquitto` with your container's name):

```bash
docker exec -it mosquitto mosquitto_passwd /mosquitto/config/passwd tesla-bridge     # <password 1>
docker exec -it mosquitto mosquitto_passwd /mosquitto/config/passwd fleet-telemetry  # <password 2>
docker restart mosquitto
```

- The file-permission warning from `mosquitto_passwd` can be ignored.
- If your broker is anonymous (`allow_anonymous true`), skip this and leave usernames and passwords empty in `.env`.
- Test a login: `docker exec mosquitto mosquitto_sub -u tesla-bridge -P '<password 1>' -t '$SYS/broker/version' -C 1`. It should print a version, not "not authorised".

### 5. The certificate for fleet-telemetry

The car talks directly to fleet-telemetry on port 4443, so it needs your domain's certificate. Put `fullchain.pem` and `privkey.pem` in `config/fleet-telemetry/certs/`.

From Nginx Proxy Manager. Adjust the path to where your NPM lives, and `npm-<nr>` to your certificate's number (see *SSL Certificates* in NPM, or `ls <npm folder>/letsencrypt/live/`):

```bash
mkdir -p config/fleet-telemetry/certs
NPM=~/docker-services/nginx-proxy-manager/letsencrypt/live/npm-<nr>   # example – adjust to yours
sudo cp -L $NPM/fullchain.pem $NPM/privkey.pem config/fleet-telemetry/certs/
sudo chown -R $(id -u):$(id -g) config/fleet-telemetry/certs
```

Let's Encrypt renews roughly every 60 days, so put the same lines in a cron job (e.g. weekly), followed by `docker compose restart fleet-telemetry`.

### 6. Start

```bash
docker compose up -d
docker compose logs tesla-init          # what was created + warnings
docker compose logs -f tesla-bridge     # A: connected to MQTT 192.168.0.x   B: embedded MQTT broker running
```

`tesla-init` runs first on every start and then exits. It creates the app key (`config/tesla-proxy/fleet-key.pem`) and the internal TLS if they are missing, and writes `config/fleet-telemetry/config.json` from `.env`. Existing keys are never touched.

**Include `config/` and `data/` in your backup.** If you lose `fleet-key.pem`, the key has to be paired with the car again.

### 7. Login, domain and pairing in the dashboard

Open `http://<server-IP>:8090` (on your local network only) and sign in with `Bridge__Web__Token`. The **Setup** panel walks you through:

1. **Register the domain with Tesla** (partner registration). Requires `https://<your domain>` to work from the internet.
2. **Sign in with Tesla**.
3. **Pair the key with the car**: scan the QR code with your phone (opens `tesla.com/_ak/<your domain>`) and approve in the Tesla app while you're at the car.
4. **Send the telemetry configuration** to the car. This also happens automatically on first start, and it renews itself (it expires after 30 days).

Open the Tesla app to wake the car. Data should show up within a minute. Check the status from a terminal:

```bash
docker compose exec tesla-bridge dotnet TeslaBridge.dll telemetry-status   # wait for "synced": true
docker compose exec tesla-bridge dotnet TeslaBridge.dll telemetry-errors   # if no data arrives
```

> The car can stream to several apps. If you use Teslemetry, it keeps working side by side. Cancel it when you're happy.

### 8A. Home Assistant via MQTT

If HA is already connected to the same broker (e.g. for Zigbee2MQTT), the devices **<your car name>** and **Tesla Bridge** appear under MQTT by themselves. Otherwise: *Settings → Devices & services → Add integration → MQTT*, and point it at the broker.

If no entities appear, check that `Bridge__Mqtt__DiscoveryPrefix=homeassistant` and that HA's MQTT integration uses the same prefix (the default).

### 8B. Home Assistant via the integration

**With HACS:**
1. HACS → ⋮ (top right) → **Custom repositories**
2. Repository: `https://github.com/Mfoldager17/tesla-bridge`, type: **Integration** → *Add*
3. Search for **Tesla Bridge** in HACS → *Download*
4. **Restart Home Assistant**
5. *Settings → Devices & services → Add integration → Tesla Bridge*
6. URL: `http://<server-IP>:8090`, token: `Bridge__Web__Token` from `.env`

**Without HACS:** copy the folder `custom_components/tesla_bridge` from this repo to HA's `config/custom_components/tesla_bridge`, restart HA and continue from point 5.

The integration:
- fetches the entities from the bridge (`/api/entities`), so new entities in a new bridge version show up when the integration is reloaded
- receives changes **live** via Server-Sent Events. It doesn't poll, and it catches up on everything that happened if the connection was lost
- marks all entities *unavailable* if the bridge is down, just like MQTT does
- asks for the new token (reauth) on the next restart if you change `Bridge__Web__Token`
- shows its setup dialog in English or Danish, depending on your HA language

---

## Switching between MQTT and the integration

Both ways can run **at the same time** while you test. The entities from the way you add last will then get `_2` in their entity id. To switch completely:

### From A (MQTT) to B (integration)

1. **Change `.env`** to B and restart, so the bridge stops publishing to your broker:
   ```ini
   Bridge__Mqtt__EmbeddedBroker=true
   Bridge__Mqtt__DiscoveryPrefix=
   ```
   ```bash
   docker compose up -d
   ```
2. **Clear the bridge's retained messages from your broker.** The broker remembers discovery configs and last values (retained). If they stay, HA may recreate the MQTT device on its next restart. The VIN must be **lowercase**:
   ```bash
   docker exec mosquitto mosquitto_sub -u tesla-bridge -P '<password 1>' \
     -t 'homeassistant/+/tesla_bridge_<vin in lowercase>/+/config' \
     -t 'homeassistant/sensor/tesla_bridge/+/config' \
     -t 'tesla/#' \
     --remove-retained --retained-only -W 3
   ```
   It ends with "Timed out" after 3 seconds. That's expected. When the discovery configs are cleared, HA removes the MQTT entities by itself. If an empty device is left under MQTT, delete it.
   Only the bridge's own topics are cleared. Zigbee2MQTT and everything else on the broker is untouched.
3. **(Optional) Remove the MQTT users.** They do no harm if they stay:
   ```bash
   docker exec mosquitto mosquitto_passwd -D /mosquitto/config/passwd tesla-bridge
   docker exec mosquitto mosquitto_passwd -D /mosquitto/config/passwd fleet-telemetry
   docker restart mosquitto
   ```
4. **Add the integration** (step 8B). The entities get the same entity ids as before, so dashboards and automations keep working.

### From B (integration) to A (MQTT)

1. Create the MQTT users (step 4).
2. Change `.env` to A (step 3) and run `docker compose up -d`.
3. Delete the **Tesla Bridge** integration in HA (*Settings → Devices & services*) so the entity ids are freed.
4. Restart the bridge (`docker compose restart tesla-bridge`). It sends discovery again, and the MQTT entities appear with the same entity ids.

---

## Entities in Home Assistant

Same names and types as Teslemetry. The prefix comes from `Bridge__Vehicles__0__Name`: "Model Y" gives `model_y_`, "My Car" gives `my_car_`. Accented and Nordic letters are simplified (æ → ae, ø → oe, å → a, é → e). The table shows the names without the prefix.

For more cars, add `Bridge__Vehicles__1__Vin` and `Bridge__Vehicles__1__Name` and so on. Each car becomes its own device with its own prefix.

| Type | Entities |
|---|---|
| `climate` | `climate` (on/off, temperature, presets off/keep/dog/camp), `cabin_overheat_protection` (off/cool/fan only, 30/35/40 °C) |
| `lock` | `lock`, `charge_cable_lock` |
| `cover` | `frunk` (open only), `trunk`, `charge_port_door`, `windows` |
| `switch` | `charge`, `sentry_mode`, `defrost`, `guest_mode`, `valet_mode`, `auto_seat_climate_left/right`, `auto_steering_wheel_heater` |
| `number` | `charge_limit`, `charge_current` |
| `select` | `seat_heater_front_left/right`, `seat_heater_rear_left/center/right`, `steering_wheel_heater` |
| `button` | `wake`, `flash_lights`, `honk_horn`, `homelink`, `keyless_driving`, `play_fart` |
| `sensor` | `battery_level`, `charging`, `charger_power/voltage/current`, `charge_energy_added`, `time_to_full_charge`, `energy_remaining`, `estimate_battery_range`, `ideal_battery_range`, `speed`, `shift_state` (P/D/R/N), `odometer`, `tire_pressure_*`, `inside/outside_temperature`, `destination`, `time_to_arrival`, `distance_to_arrival`, `state_of_charge_at_arrival`, `version`, `last_seen`, `command_status` … |
| `binary_sensor` | `status` (online), `charge_cable`, doors, windows, `driver_seat_occupied`, `driver_seat_belt`, `preconditioning_enabled`, `scheduled_charging_pending`, `fast_charger_present` |
| `device_tracker` | `location` (the car), `route` (destination from the navigation) |

There is also a **Tesla Bridge** device with your Fleet API usage: `sensor.tesla_bridge_cost_month`, `…_cost_projected`, `…_credit_left`, plus counts of signals, commands, wakes and data requests for the month. Handy for an automation that warns you if the projected cost approaches $10.

`sensor.<car>_command_status` shows the latest command (`pending` → `waking` → `succeeded`/`failed`) with the error as an attribute.

---

## Updating

**The bridge** (both ways):
```bash
cd ~/docker-services/tesla-bridge
docker compose pull && docker compose up -d
```
New versions are backwards compatible with your existing `.env`: new settings have defaults that keep the current behaviour.
To pin a version, set `BRIDGE_TAG=v1.2.0` in `.env`. Roll back by setting an older tag.
Changes to `docker-compose.yml` itself (rare) are fetched with the same `curl` as in step 3. Your `.env` is not overwritten.

**The integration** (way B): HACS shows an update when there are new commits. Install it and restart HA. New entities from the bridge appear when the integration is reloaded (*⋮ → Reload*).

---

## Troubleshooting

| Symptom | Check |
|---|---|
| No data from the car | `telemetry-status` must show `"synced": true`. Run `telemetry-errors`. Is port 4443 open from outside? Is the certificate in `config/fleet-telemetry/certs/`? `docker compose logs fleet-telemetry` |
| A: "not authorised" in the log | The user/password in `.env` doesn't match Mosquitto's `passwd`. Test with `mosquitto_sub` (step 4) |
| A: No entities in HA | `Bridge__Mqtt__DiscoveryPrefix` must be `homeassistant`, and HA must be on the same broker |
| B: "Cannot reach the bridge" in HA | Use the server's IP, not `localhost`. Try `curl http://<server-IP>:8090/api/info` from the HA machine. `Bridge__Web__Enabled` must not be `false` |
| B: "Wrong token" | The token must be exactly `Bridge__Web__Token` from `.env` |
| Entities end in `_2` | Both ways are running at once. See [Switching](#switching-between-mqtt-and-the-integration) |
| Domain or app name rejected by Tesla | It contains "tesla". Pick another (sub)domain |
| Commands fail with "not logged in" | Sign in with Tesla in the dashboard (step 7) |
| Commands fail with "key not paired" | Pair the key again via the QR code (step 7) while you're at the car |
| "Bil: venter på signal" (car: waiting for signal) | The car is asleep. The bridge asks Tesla on start; when the car wakes, data arrives |
| Values are "unknown" | The car only sends on change. When it wakes, a snapshot is fetched (at most once an hour; see Settings). Check that the field isn't turned off under Settings |
| Streaming costs too much | Settings → **Save money**, or make the fields marked *changes often* less frequent |

---

## The dashboard

The bridge has a built-in dashboard at `http://<server-IP>:8090` (`http://localhost:8080` when testing locally). It works on a phone too, follows your system's light/dark mode, and is in English or Danish (switch in the sidebar).

- **Car**: a top-down view of the car that shows open doors, frunk, trunk, windows and the charge cable live, the battery with range and charge limit, quick actions, and the latest commands with their status (*sent → waking the car → done/failed*)
- **Controls**: everything you can do from Home Assistant – climate, Keep/Dog/Camp, seat and steering wheel heat, charging, locks, sentry, guest/valet mode, frunk, trunk, windows, HomeLink. Purple means the bridge is waiting for the car to confirm
- **Data**: every value with its source (`stream`, `snapshot`, `optimistic`, `restored`, `bridge`) and age, the raw telemetry exactly as the car sent it, and the telemetry status on the car
- **Usage**: the month's signals, commands, wakes and data requests, priced at Tesla's rates, with a projection to the end of the month
- **Settings**: how often the car sends each value, and how often a snapshot is fetched – see [Fields and intervals](#fields-and-intervals)
- **Setup**: domain, Tesla login, pairing and telemetry status, step by step

The dashboard can unlock your car. Always set `Bridge__Web__Token`, and **never forward port 8090**. The public port 8091 only answers with the public key and the login callback. `Bridge__Web__Enabled=false` turns off the dashboard and the HTTP API – way B then stops working.

---

## Fields and intervals

Open **Settings** in the dashboard to choose which values the car sends and how often at most. The car **only sends a value when it changes**, so:
- discrete values (lock, doors, climate, charging state, gear) are set to 1 s and feel live at no extra cost
- values that change all the time while driving or charging (location, speed, battery, charging power, temperatures, tire pressure) are the ones that cost. They are marked *changes often*

The page shows how many signals each field has sent this month and estimates the monthly streaming cost as you change things. Three quick choices set all the costly fields at once: **Save money**, **Recommended** (the defaults) and **As live as possible**. Turning a field off stops the car from sending it, and the matching entity in Home Assistant stops updating.

**Save and send to car** stores the settings in `data/settings.json` (kept across updates) and sends the new configuration to the car right away. If the car or Tesla can't be reached, the bridge sends it on its next check (within 12 hours). **Reset to defaults** goes back to the intervals shipped with the bridge.

The same page sets how often the bridge fetches a **snapshot** from the Fleet API when the car wakes up (default: at most once an hour, or never). A snapshot fills in values the car hasn't streamed yet, costs one data request and never wakes the car.

The defaults live in `config/bridge/telemetry-fields.json` in the image. Fields the bridge doesn't know pass through unchanged as `snake_case` (e.g. `TpmsPressureFl` → `tpms_pressure_fl`). Valid names are in Tesla's [vehicle_data.proto](https://github.com/teslamotors/fleet-telemetry/blob/main/protos/vehicle_data.proto).

---

## MQTT API (way A)

**State** (retained, JSON values, metric units):
```
tesla/<VIN>/state/battery_level        72.5
tesla/<VIN>/state/climate_on           true
tesla/<VIN>/state/charging_state       "charging"
tesla/<VIN>/state/gear                 "D"
tesla/<VIN>/state/location             {"latitude":56.1,"longitude":10.1}
tesla/<VIN>/state/locked_motion        "LOCKING"   (also *_motion for frunk, trunk, charge port and windows)
tesla/<VIN>/json                       full snapshot with updated_at and source per field
tesla/<VIN>/command/status             {"command":"ClimateOn","status":"waking",...}
tesla/bridge/usage                     Fleet API usage for the month
tesla/bridge/status                    online | offline
```
`source` is `stream` (confirmed by the car), `snapshot` (fetched from the Fleet API), `optimistic` (waiting for confirmation), `restored` (from disk) or `bridge`.

**Commands**: publish to `tesla/<VIN>/cmd/<name>`:

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
mosquitto_pub -u tesla-bridge -P '<password 1>' -t tesla/<VIN>/cmd/climate -m on
```

---

## HTTP API (way B and everything else)

This is the API the integration and the dashboard use. It runs on port 8090 in both ways, so ESP32 panels, Node-RED etc. can use it without MQTT too.
Token: `Authorization: Bearer <token>` (or `?token=` in the URL).

| Endpoint | |
|---|---|
| `GET /api/info` | start time, cars, whether MQTT is connected, whether a token is required (needs no token itself) |
| `GET /api/entities` | all HA entities: the same definitions as MQTT discovery, with `state:<field>` / `cmd:<name>` instead of topics |
| `GET /api/state` | all fields per car with value, timestamp and source (including `*_motion`) |
| `GET /api/events` | Server-Sent Events: `field` (a value changed) and `command` (command status), live |
| `POST /api/vehicles/<VIN>/cmd/<name>` | a command. Body = the same payload as on MQTT (empty body = `PRESS`) |
| `GET /api/usage` | Fleet API usage |
| `GET /api/settings` | telemetry fields with intervals, on/off and this month's signal count, plus the snapshot interval |
| `PUT /api/settings` | change them: `{"fields": {"Location": {"enabled": true, "interval_seconds": 10}}, "snapshot_interval_minutes": 60}` – saved and sent to the car |
| `POST /api/settings/reset` | back to the defaults |

```bash
T=<token>
curl -H "Authorization: Bearer $T" http://<server-IP>:8090/api/state
curl -H "Authorization: Bearer $T" -X POST -d on http://<server-IP>:8090/api/vehicles/<VIN>/cmd/climate
curl -N -H "Authorization: Bearer $T" http://<server-IP>:8090/api/events      # live
```

---

## Testing locally without a car

Only needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
cd src/TeslaBridge
dotnet run
```

Open **http://localhost:8080** and press **Send testdata** (send test data). The local test profile (`appsettings.Development.json`) starts the **embedded broker** (like way B) and a **simulator** that sends fake telemetry through MQTT exactly like fleet-telemetry. Commands fail gracefully with "not logged in to Tesla".

You can also point a test HA with the integration at `http://<your machine>:8080`.

> If something already uses port 1883, run `Bridge__Mqtt__Port=1884 dotnet run`.

---

## Development

```
tesla-bridge/
├── docker-compose.yml              all the server needs (+ .env)
├── docker-compose.build.yml        build the bridge locally instead of pulling from ghcr.io
├── Dockerfile                      amd64 + arm64 (cross-compiled)
├── .env.example                    everything specific to you: domain, app, VIN, MQTT/embedded broker
├── hacs.json                       makes the repo installable from HACS
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
│   ├── Publishing/                 HomeAssistantDiscovery (the entities – used by BOTH ways), StatePublisher
│   ├── Mqtt/                       connection with reconnect and Last Will, embedded broker
│   ├── Settings/                   settings page: stored settings + description of every telemetry field
│   ├── Web/                        dashboard (wwwroot/index.html), HTTP API, live events (SSE)
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
