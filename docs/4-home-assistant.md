<sub>[Tesla Bridge](../README.md) · [Guide](README.md) · [Before you start](1-before-you-start.md) → [Install](2-install.md) → [Connect the car](3-connect-the-car.md) → **Home Assistant** → [The dashboard](5-the-dashboard.md)</sub>

# 4. Home Assistant

Follow the way you chose in [step 1](1-before-you-start.md#choice-2-how-home-assistant-gets-the-data). The entities are the same either way – see [Entities](entities.md).

## A) MQTT

If HA is already connected to the same broker (e.g. for Zigbee2MQTT), the devices **<your car name>** and **Tesla Bridge** appear under MQTT by themselves. Otherwise: *Settings → Devices & services → Add integration → MQTT*, and point it at the broker.

If no entities appear, check that `Bridge__Mqtt__DiscoveryPrefix=homeassistant` and that HA's MQTT integration uses the same prefix (the default).

## B) The Tesla Bridge integration

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

## Want to switch later?

Both can run side by side while you test. [Switching between MQTT and the integration](switching.md) explains how to move completely, including clearing old MQTT messages.

---

**Next:** [5. The dashboard →](5-the-dashboard.md)
