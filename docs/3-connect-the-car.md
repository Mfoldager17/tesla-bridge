<sub>[Tesla Bridge](../README.md) · [Guide](README.md) · [Before you start](1-before-you-start.md) → [Install](2-install.md) → **Connect the car** → [Home Assistant](4-home-assistant.md) → [The dashboard](5-the-dashboard.md)</sub>

# 3. Connect the car

Open `http://<server-IP>:8090` (on your local network only) and sign in with `Bridge__Web__Token`. The **Setup** panel walks you through:

1. **Register the domain with Tesla** (partner registration). Happens by itself once `https://<your domain>` works from the internet – the button is there if you want to do it now.
2. **Sign in with Tesla**.
3. **Pair the key with the car**: scan the QR code with your phone (opens `tesla.com/_ak/<your domain>`) and approve in the Tesla app while you're at the car.
4. **Send the telemetry configuration** to the car. This also happens automatically on first start, and it renews itself (it expires after 30 days).

Open the Tesla app to wake the car. Data should show up within a minute. Check the status from a terminal:

```bash
docker compose exec tesla-bridge dotnet TeslaBridge.dll telemetry-status   # wait for "synced": true
docker compose exec tesla-bridge dotnet TeslaBridge.dll telemetry-errors   # if no data arrives
```

> The car can stream to several apps. If you use Teslemetry, it keeps working side by side. Cancel it when you're happy.

If nothing arrives, see [Troubleshooting](troubleshooting.md).

---

**Next:** [4. Home Assistant →](4-home-assistant.md) – or skip it: everything also works from [the dashboard](5-the-dashboard.md) alone.
