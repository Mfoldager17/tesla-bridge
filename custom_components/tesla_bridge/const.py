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
]

# Key in hub.state for the bridge's own usage (not a car)
BRIDGE = "__bridge__"
