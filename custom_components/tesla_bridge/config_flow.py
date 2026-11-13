"""Setup in the HA UI: bridge address + optional token."""

from __future__ import annotations

from typing import Any

from homeassistant.config_entries import ConfigFlow, ConfigFlowResult
from homeassistant.helpers.aiohttp_client import async_get_clientsession
import voluptuous as vol

from .api import BridgeAuthError, BridgeClient, BridgeError
from .const import CONF_TOKEN, CONF_URL, DOMAIN


class TeslaBridgeConfigFlow(ConfigFlow, domain=DOMAIN):
    VERSION = 1

    async def _validate(self, url: str, token: str | None) -> tuple[dict[str, str], dict[str, Any] | None]:
        client = BridgeClient(async_get_clientsession(self.hass), url, token)
        try:
            info = await client.info()
            await client.entities()   # requires the token if the bridge has one
        except BridgeAuthError:
            return {"base": "invalid_auth"}, None
        except BridgeError:
            return {"base": "cannot_connect"}, None
        return {}, info

    async def async_step_user(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        errors: dict[str, str] = {}
        if user_input is not None:
            url = user_input[CONF_URL].strip().rstrip("/")
            if not url.startswith(("http://", "https://")):
                url = f"http://{url}"
            token = (user_input.get(CONF_TOKEN) or "").strip() or None
            errors, info = await self._validate(url, token)
            if not errors and info is not None:
                vins = sorted(v["vin"] for v in info.get("vehicles", []))
                await self.async_set_unique_id(vins[0] if vins else url)
                self._abort_if_unique_id_configured(updates={CONF_URL: url, CONF_TOKEN: token})
                names = ", ".join(v["name"] for v in info.get("vehicles", [])) or "Tesla Bridge"
                return self.async_create_entry(title=names, data={CONF_URL: url, CONF_TOKEN: token})

        schema = vol.Schema(
            {
                vol.Required(CONF_URL, default=(user_input or {}).get(CONF_URL, "http://")): str,
                vol.Optional(CONF_TOKEN): str,
            }
        )
        return self.async_show_form(step_id="user", data_schema=schema, errors=errors)

    async def async_step_reauth(self, entry_data: dict[str, Any]) -> ConfigFlowResult:
        return await self.async_step_reauth_confirm()

    async def async_step_reauth_confirm(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        entry = self._get_reauth_entry()
        errors: dict[str, str] = {}
        if user_input is not None:
            token = (user_input.get(CONF_TOKEN) or "").strip() or None
            errors, _ = await self._validate(entry.data[CONF_URL], token)
            if not errors:
                return self.async_update_reload_and_abort(entry, data_updates={CONF_TOKEN: token})
        return self.async_show_form(
            step_id="reauth_confirm", data_schema=vol.Schema({vol.Optional(CONF_TOKEN): str}), errors=errors
        )
