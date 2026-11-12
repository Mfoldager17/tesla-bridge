<sub>[Tesla Bridge](../README.md) · [Guide](README.md) · Reference · Updating</sub>

# Updating

**The bridge** (both ways):
```bash
cd ~/docker-services/tesla-bridge
docker compose pull && docker compose up -d
```
New versions are backwards compatible with your existing `.env`: new settings have defaults that keep the current behaviour.
To pin a version, set `BRIDGE_TAG=v1.2.0` in `.env`. Roll back by setting an older tag.
Changes to `docker-compose.yml` itself (rare) are fetched with the same `curl` as in [install, step 3](2-install.md#step-3--download-the-compose-file-and-create-env) – or just run the [setup script](2-install.md#with-the-setup-script) again. Your `.env` is not overwritten.

**The integration** (way B): HACS shows an update when there are new commits. Install it and restart HA. New entities from the bridge appear when the integration is reloaded (*⋮ → Reload*).
