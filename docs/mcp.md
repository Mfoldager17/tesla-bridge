<sub>[Tesla Bridge](../README.md) · [Guide](README.md) · Optional · AI assistants (MCP)</sub>

# AI assistants (MCP)

**Optional, off by default.** The bridge can be an [MCP](https://modelcontextprotocol.io) server, so an AI assistant such as Claude can answer *"is the car charging?"*, *"where is it?"* or *"turn the climate on, 21 degrees"* – straight from the bridge, without Home Assistant.

## Turn it on

1. Dashboard → **Settings → AI assistants (MCP)** → **On**.
2. Press **Make a token** and copy it. It is shown only this once; the bridge keeps only its fingerprint (SHA-256). It's a token of its own, not the dashboard's, so you can give an assistant access and take it back (**Revoke the token**) without changing anything else.
3. Connect the assistant. The panel shows the exact lines with your address and token filled in:

**Claude Code**

```bash
claude mcp add --transport http tesla-bridge http://<server-IP>:8090/mcp \
  --header "Authorization: Bearer <token>"
```

**Claude Desktop** – in `claude_desktop_config.json` (needs Node.js):

```json
{
  "mcpServers": {
    "tesla-bridge": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "http://<server-IP>:8090/mcp", "--header", "Authorization: Bearer <token>"]
    }
  }
}
```

The assistant must run on a computer that can reach the dashboard: on your network, or over a VPN such as Tailscale. claude.ai in the browser can't, because the dashboard is deliberately not on the internet.

## What the assistant can do

| Tool | |
|---|---|
| `list_cars` | the cars, whether they are online, when they were last heard from |
| `get_car_status` | battery and range, charging, locks, doors, windows, climate and temperatures, sentry, driving and navigation, odometer, software, and where the car is – with the street address when the map is on |
| `get_car_fields` | every value the bridge knows, with when and where it came from |
| `get_usage` | this month's Fleet API cost, the projection and what Bluetooth saved |
| `run_command` | a command, waiting until the car has done it (waking it first if needed): climate, temperature, keep/dog/camp, defrost, seat and steering wheel heat, cabin overheat protection, charging, charge limit and current, close the charge port, lock, close the windows, sentry on, flash the lights, honk, wake |

Commands go the same way as from the dashboard: over Bluetooth when the [ESP32](ble.md) has the car in range, otherwise the Fleet API. They show up in the command log and in Home Assistant like any other.

## Commands that open the car

Unlocking, opening the frunk, trunk, windows or charge port, turning sentry off, guest and valet mode, HomeLink and keyless driving are **refused** unless you choose **Commands that open the car → Allowed**. Only do that if you trust every assistant you give the token to. Your assistant will normally also ask you before it runs a tool.

## Good to know

- **Turned off**, `/mcp` answers 404; **without the right token**, 401. A new token replaces the old one at once.
- Commands that cost money still cost money: a command over the Fleet API is $1 per 1,000, a wake-up $1 per 50 (see the *Usage* page).
- MCP doesn't change the dashboard's or Home Assistant's access – they keep their own token.

---

**Back:** [The dashboard](5-the-dashboard.md) · [Guide](README.md)
