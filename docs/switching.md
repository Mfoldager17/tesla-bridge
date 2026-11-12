<sub>[Tesla Bridge](../README.md) · [Guide](README.md) · Reference · Switching</sub>

# Switching between MQTT and the integration

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
   docker exec mosquitto mosquitto_passwd -D /mosquitto/config/passwd tesla-bridge      # your password file
   docker exec mosquitto mosquitto_passwd -D /mosquitto/config/passwd fleet-telemetry
   docker kill -s HUP mosquitto
   ```
4. **Add the integration** ([step 4B](4-home-assistant.md#b-the-tesla-bridge-integration)). The entities get the same entity ids as before, so dashboards and automations keep working.

### From B (integration) to A (MQTT)

1. Create the MQTT users ([install, step 4](2-install.md#step-4--mqtt-users-a-only)) – or run the setup script and pick your Mosquitto.
2. Change `.env` to A ([install, step 3](2-install.md#step-3--download-the-compose-file-and-create-env)) and run `docker compose up -d`.
3. Delete the **Tesla Bridge** integration in HA (*Settings → Devices & services*) so the entity ids are freed.
4. Restart the bridge (`docker compose restart tesla-bridge`). It sends discovery again, and the MQTT entities appear with the same entity ids.
