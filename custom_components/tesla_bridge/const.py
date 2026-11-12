"""Constants for Tesla Bridge."""

DOMAIN = "tesla_bridge"
CONF_URL = "url"
CONF_TOKEN = "token"

PLATFORMS = [
    "binary_sensor",
    "button",
    "climate",
    "cover",
    "device_tracker",
    "lock",
    "number",
    "select",
    "sensor",
    "switch",
    "update",
]

# The oldest bridge this integration works with. A newer bridge always works (the API only grows),
# so this only goes up when the integration starts using something new.
MIN_BRIDGE_VERSION = "1.0.0"

# Key in hub.state for the bridge's own usage (not a car)
BRIDGE = "__bridge__"
