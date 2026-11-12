<sub>[Tesla Bridge](../README.md) · [Guide](README.md) · Reference · Updating</sub>

# Versions and updating

Tesla Bridge has version numbers like `1.2.0` ([semantic versioning](https://semver.org)):

| Change | Example | What it means for you |
|---|---|---|
| **patch** | 1.2.**0** → 1.2.**1** | bug fixes only |
| **minor** | 1.**2**.0 → 1.**3**.0 | new features. Your `.env`, entities and automations keep working |
| **major** | **1**.3.0 → **2**.0.0 | something you have to act on. The release notes say exactly what |

The bridge (Docker image) and the integration (HACS) are released together with the same number. They don't have to match, though: the integration only needs a *minimum* bridge version, and a newer bridge always works with an older integration.

What changed in each version: [CHANGELOG.md](../CHANGELOG.md) and [Releases](https://github.com/Mfoldager17/tesla-bridge/releases).

## Is there a new version?

You don't have to check yourself. The bridge looks up the newest release on GitHub every 6 hours (one anonymous request) and shows it:

- **Home Assistant:** *Settings → Updates* lists **Tesla Bridge 1.3.0** with the release notes, both with MQTT and with the integration (`update.tesla_bridge`). There is no install button: the bridge runs in Docker, so you update it as shown below.
- **Dashboard:** the version sits at the bottom of the menu (and under *Setup* on a phone), with a link when a new one is out.
- **The devices in HA** show the running version as *Firmware*.

Turn the lookup off with `Bridge__Updates__Check=false` in `.env`.

## Update the bridge

```bash
cd ~/tesla-bridge        # your folder
docker compose pull && docker compose up -d
docker image prune -f    # removes the old image layers, so they don't fill the disk
```

New versions keep working with your existing `.env`: new settings have defaults that keep the current behaviour.

Changes to `docker-compose.yml` itself are rare, and the release notes mention them. Fetch it with the same `curl` as in [install, step 3](2-install.md#step-3--download-the-compose-file-and-create-env), or run the [setup script](2-install.md#with-the-setup-script) again. Your `.env` is not overwritten.

## Choose what you run: `BRIDGE_TAG`

`BRIDGE_TAG` in `.env` picks the image:

| `BRIDGE_TAG=` | You get | For |
|---|---|---|
| `latest` (default) | the newest **release** | almost everyone |
| `v1` | the newest 1.x release – never a major change by surprise | careful updating |
| `v1.2` | the newest 1.2.x – bug fixes only | very careful updating |
| `v1.2.0` | exactly that version | pinning; roll back by setting an older one |
| `edge` | every change on `main` as soon as it's built, between releases | testing new things early |

After changing it: `docker compose pull && docker compose up -d`.

## The integration (way B)

HACS shows a new release under *Settings → Updates* with its release notes. Install it and restart Home Assistant. To go back, use HACS → Tesla Bridge → ⋮ → *Redownload* and pick a version.

If the integration ever needs a newer bridge than you run, Home Assistant shows a **repair** (*Settings → Repairs*) saying which version is needed. Update the bridge as above.

New entities from a newer bridge appear when the integration is reloaded (*⋮ → Reload*).
