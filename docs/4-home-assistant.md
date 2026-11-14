<sub>[T-Pitwall](../README.md) · [Guide](README.md) · [Before you start](1-before-you-start.md) → [Install](2-install.md) → [Connect the car](3-connect-the-car.md) → **Home Assistant** → [The dashboard](5-the-dashboard.md)</sub>

# 4. Home Assistant

Follow the way you chose in [step 1](1-before-you-start.md#choice-2-how-home-assistant-gets-the-data). The entities are the same either way – see [Entities](entities.md).

## A) MQTT

If HA is already connected to the same broker (e.g. for Zigbee2MQTT), the devices **<your car name>** and **T-Pitwall** appear under MQTT by themselves. Otherwise: *Settings → Devices & services → Add integration → MQTT*, and point it at the broker.

If no entities appear, check that `TPitwall__Mqtt__DiscoveryPrefix=homeassistant` and that HA's MQTT integration uses the same prefix (the default).

## B) The T-Pitwall integration

The integration has its own repository and its own releases: **[Mfoldager17/ha-t-pitwall](https://github.com/Mfoldager17/ha-t-pitwall)**.

1. Home Assistant → **Marketplace** → **Add from link** → `https://github.com/Mfoldager17/ha-t-pitwall` (before Home Assistant 2026.11: HACS → ⋮ → *Custom repositories*, type *Integration*)
2. Install **T-Pitwall**
3. **Restart Home Assistant**
4. *Settings → Devices & services → Add integration → T-Pitwall*
5. URL: `http://<server-IP>:8090`, token: `TPitwall__Web__Token` from `.env` – or, if you didn't set one, the token the bridge made: `docker compose exec t-pitwall cat /data/web-token`

**Without the Marketplace:** copy the folder `custom_components/t_pitwall` from [ha-t-pitwall](https://github.com/Mfoldager17/ha-t-pitwall) to HA's `config/custom_components/t_pitwall`, restart HA and continue from point 4.

**Coming from Tesla Bridge (before 3.0)?** Delete the old *Tesla Bridge* integration first, so the entity ids are free. [How](updating.md#updating-to-30-tesla-bridge-is-now-t-pitwall)

The integration:
- fetches the entities from the bridge (`/api/entities`), so new entities in a new bridge version show up when the integration is reloaded
- receives changes **live** via Server-Sent Events. It doesn't poll, and it catches up on everything that happened if the connection was lost
- marks all entities *unavailable* if the bridge is down, just like MQTT does
- asks for the new token (reauth) on the next restart if you change `TPitwall__Web__Token`
- shows its setup dialog in English or Danish, depending on your HA language

## Want to switch later?

Both can run side by side while you test. [Switching between MQTT and the integration](switching.md) explains how to move completely, including clearing old MQTT messages.

---

**Next:** [5. The dashboard →](5-the-dashboard.md)
