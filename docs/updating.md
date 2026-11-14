<sub>[T-Pitwall](../README.md) · [Guide](README.md) · Reference · Updating</sub>

# Versions and updating

T-Pitwall has version numbers like `1.2.0` ([semantic versioning](https://semver.org)):

| Change | Example | What it means for you |
|---|---|---|
| **patch** | 1.2.**0** → 1.2.**1** | bug fixes only |
| **minor** | 1.**2**.0 → 1.**3**.0 | new features. Your `.env`, entities and automations keep working |
| **major** | **1**.3.0 → **2**.0.0 | something you have to act on. The release notes say exactly what |

The bridge (Docker image, this repository) and the Home Assistant integration ([ha-t-pitwall](https://github.com/Mfoldager17/ha-t-pitwall), from the Marketplace) have their own version numbers and are updated separately. They don't have to match: the integration only needs a *minimum* bridge version, and a newer bridge always works with an older integration.

What changed in each version: [CHANGELOG.md](../CHANGELOG.md) and [Releases](https://github.com/Mfoldager17/t-pitwall/releases).

## Is there a new version?

You don't have to check yourself. The bridge looks up the newest release on GitHub every 6 hours (one anonymous request) and shows it:

- **Home Assistant:** *Settings → Updates* lists **T-Pitwall (server) 1.3.0** with the release notes, both with MQTT and with the integration (`update.t_pitwall`). There is no install button: the bridge runs in Docker, so you update it as shown below.
- **Dashboard:** the version sits at the bottom of the menu (and under *Setup* on a phone), with a link when a new one is out.
- **The devices in HA** show the running version as *Firmware*.

Turn the lookup off with `TPitwall__Updates__Check=false` in `.env`.

## Update the bridge

```bash
cd ~/t-pitwall        # your folder
docker compose pull && docker compose up -d
docker image prune -f    # removes the old image layers, so they don't fill the disk
```

New versions keep working with your existing `.env`: new settings have defaults that keep the current behaviour.

Changes to `docker-compose.yml` itself are rare, and the release notes mention them. Fetch it with the same `curl` as in [install, step 3](2-install.md#step-3--download-the-compose-file-and-create-env), or run the [setup script](2-install.md#with-the-setup-script) again. Your `.env` is not overwritten.

### Updating to 3.0: Tesla Bridge is now T-Pitwall

The project has a new name everywhere: the image, the repository, the containers, the settings in `.env` and the MQTT topics. Your keys, Tesla sign-in and data stay as they are.

1. **Rename the settings in your `.env`.** `Bridge__…` is now `TPitwall__…`, and `BRIDGE_TAG` is now `TPITWALL_TAG`:
   ```bash
   cd ~/tesla-bridge        # your folder
   sed -i 's/^\(# *\)\?Bridge__/\1TPitwall__/; s/^BRIDGE_TAG=/TPITWALL_TAG=/' .env
   ```
   Was it `BRIDGE_TAG=v2`? Set `TPITWALL_TAG=v3` or `latest`.
2. **Fetch the new `docker-compose.yml`** (as above). The image is now `ghcr.io/mfoldager17/t-pitwall`, and the services are `t-pitwall` and `t-pitwall-init`.
3. **Start it and remove the old containers:**
   ```bash
   docker compose pull && docker compose up -d --remove-orphans
   ```
4. **Embedded MQTT broker?** fleet-telemetry now reaches it as `t-pitwall`. Run `docker compose restart fleet-telemetry` once.
5. Commands are now `docker compose exec t-pitwall …` (e.g. `cat /data/web-token`).

**Home Assistant with MQTT:** nothing to do. On its first start, T-Pitwall empties everything Tesla Bridge published, so Home Assistant removes those entities, and then publishes the new ones. The car's entities get the same ids (`sensor.model_y_battery_level` …), so automations, dashboards and history keep working. The usage device's entities are renamed: `sensor.tesla_bridge_cost_month` becomes `sensor.t_pitwall_cost_month`, `update.tesla_bridge` becomes `update.t_pitwall`, and so on.

The topics are now `t-pitwall/<VIN>/…` instead of `tesla/<VIN>/…`. If something other than Home Assistant reads or sends there (an ESP32 panel, Node-RED …), change it too. Tesla Bridge's last values stay on your broker until you clear them (optional):

```bash
docker exec mosquitto mosquitto_sub -u <user> -P '<password>' -t 'tesla/#' --remove-retained --retained-only -W 3
```

**Home Assistant with the integration:** the integration is now [ha-t-pitwall](https://github.com/Mfoldager17/ha-t-pitwall) with the domain `t_pitwall`, so Home Assistant sees it as a new integration. Move it once:

1. In *Settings → Devices & services*, delete the old **Tesla Bridge** integration. That removes its entities, so the new ones can get the same entity ids.
2. In the Marketplace, remove **Tesla Bridge**, add `https://github.com/Mfoldager17/ha-t-pitwall` (*Add from link*) and install **T-Pitwall**.
3. Restart Home Assistant, then add the **T-Pitwall** integration with the same URL and token.

As with MQTT, the car's entities keep their ids, and only the usage device's entities are renamed.

**MCP:** nothing to do. The address is the same; the name `tesla-bridge` in your assistant's config is only a label – rename it if you like.

### Updating to 2.0

2.0 tightens security (see [Security](security.md)), which is why it is a major version: a few things need you once.

1. **`TPITWALL_TAG=v1` in your `.env`?** It stays on 1.x on purpose. Set it to `v2` or `latest`.
2. **The dashboard always needs a token now.** If `TPitwall__Web__Token` was empty in your `.env`, the bridge makes one at the first start and prints it in the log; show it with `docker compose exec t-pitwall cat /data/web-token`. The dashboard asks for it once. The Home Assistant integration asks for it too (*Settings → Repairs* or a notification: *Reconfigure*).
3. **Fetch the new `docker-compose.yml`** (as above). It gives the bridge container only what it needs – no private keys – and drops all Linux capabilities. The old file keeps working, without those protections.
4. **Embedded MQTT broker without a password for fleet-telemetry?** The broker no longer accepts anonymous logins; the init container makes a password. Run `docker compose restart fleet-telemetry` once after the update.
5. **ESP32:** `tesla-ble.yaml` now requires a web password. Add `web_username` and `web_password` to its `secrets.yaml`, flash it again, and type the same on the dashboard (*Settings → Bluetooth*).

Also new in 2.0, nothing to do: a new dashboard with a map and the car's street address, and [AI assistants over MCP](mcp.md) (off until you turn it on).

## Choose what you run: `TPITWALL_TAG`

`TPITWALL_TAG` in `.env` picks the image:

| `TPITWALL_TAG=` | You get | For |
|---|---|---|
| `latest` (default) | the newest **release** | almost everyone |
| `v3` | the newest 3.x release – never a major change by surprise | careful updating |
| `v3.1` | the newest 3.1.x – bug fixes only | very careful updating |
| `v3.1.0` | exactly that version | pinning; roll back by setting an older one |
| `edge` | every change on `main` as soon as it's built, between releases | testing new things early |

After changing it: `docker compose pull && docker compose up -d`.

## The integration (way B)

The integration is updated in Home Assistant, not on the server: the Marketplace shows a new release under *Settings → Updates* with its release notes. Install it and restart Home Assistant. Its changes are in [its own changelog](https://github.com/Mfoldager17/ha-t-pitwall/blob/main/CHANGELOG.md).

If the integration ever needs a newer bridge than you run, Home Assistant shows a **repair** (*Settings → Repairs*) saying which version is needed. Update the bridge as above.

New entities from a newer bridge appear when the integration is reloaded (*⋮ → Reload*) – no new integration needed.
