<sub>[Tesla Bridge](../README.md) · [Guide](README.md) · Reference · Troubleshooting</sub>

# Troubleshooting

| Symptom | Check |
|---|---|
| No data from the car | `telemetry-status` must show `"synced": true`. Run `telemetry-errors`. Is port 4443 open from outside? Is the certificate in `config/fleet-telemetry/certs/`? `docker compose logs fleet-telemetry` |
| A: "not authorised" in the log | The user/password in `.env` doesn't match Mosquitto's `passwd`. Test with `mosquitto_sub` ([install, step 4](2-install.md#step-4--mqtt-users-a-only)) |
| A: No entities in HA | `Bridge__Mqtt__DiscoveryPrefix` must be `homeassistant`, and HA must be on the same broker |
| B: "Cannot reach the bridge" in HA | Use the server's IP, not `localhost`. Try `curl http://<server-IP>:8090/api/info` from the HA machine. `Bridge__Web__Enabled` must not be `false` |
| B: "Wrong token" | The token must be exactly `Bridge__Web__Token` from `.env` |
| Entities end in `_2` | Both ways are running at once. See [Switching](switching.md) |
| Domain or app name rejected by Tesla | It contains "tesla". Pick another (sub)domain |
| Public key not reachable / domain won't register | `curl https://<your domain>/.well-known/appspecific/com.tesla.3p.public-key.pem` from outside must print a key. Reverse proxy: the host must forward to `<server-IP>:8091`. Tunnel: the public hostname must point to `http://<server-IP>:8091`, and `Bridge__Tesla__PartnerDomain` must be that name |
| Tunnel: the car never streams | The car can't use a name that goes through the tunnel. `Bridge__Telemetry__Hostname` must be a separate DNS-only name with port 4443 forwarded, and the certificate must be for *that* name ([choice 1](1-before-you-start.md#choice-1-how-the-internet-reaches-you)) |
| `telemetry-errors` mentions TLS / certificate | The certificate in `config/fleet-telemetry/certs/` isn't valid for `Bridge__Telemetry__Hostname` or has expired. Check with `openssl x509 -in config/fleet-telemetry/certs/fullchain.pem -noout -enddate -ext subjectAltName`. Is `renew-cert.sh` in root's crontab (`sudo crontab -l`)? |
| No Nginx Proxy Manager | Not needed. Any reverse proxy or tunnel works – you just need a certificate and to know its folder ([step 5](2-install.md#step-5--the-certificate-for-fleet-telemetry)) |
| Commands fail with "not logged in" | Sign in with Tesla in the dashboard ([3. Connect the car](3-connect-the-car.md)) |
| Commands fail with "key not paired" | Pair the key again via the QR code ([3. Connect the car](3-connect-the-car.md)) while you're at the car |
| "Bil: venter på signal" (car: waiting for signal) | The car is asleep. The bridge asks Tesla on start; when the car wakes, data arrives |
| Values are "unknown" | The car only sends on change. When it wakes, a snapshot is fetched (at most once an hour; see Settings). Check that the field isn't turned off under Settings |
| Streaming costs too much | Settings → **Save money**, or make the fields marked *changes often* less frequent |

Still stuck? The dashboard's **Data → Raw telemetry** shows exactly what the car sends, and `docker compose logs tesla-bridge` what the bridge does.
