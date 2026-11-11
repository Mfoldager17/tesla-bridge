"""Konstanter for Tesla Bridge."""

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

# Nøgle i hub.state for bridgens eget forbrug (ikke en bil)
BRIDGE = "__bridge__"
