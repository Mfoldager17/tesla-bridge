"""HTTP client for the bridge API (the same one the dashboard uses)."""

from __future__ import annotations

import asyncio
from collections.abc import AsyncIterator
import json
from typing import Any

import aiohttp


class BridgeError(Exception):
    """The bridge could not be reached."""


class BridgeAuthError(BridgeError):
    """Wrong or missing token."""


class BridgeClient:
    """GET /api/info, /api/entities, /api/state, /api/usage, SSE /api/events and POST commands."""

    def __init__(self, session: aiohttp.ClientSession, url: str, token: str | None) -> None:
        self._session = session
        self.url = url.rstrip("/")
        self._token = token or None

    @property
    def _headers(self) -> dict[str, str]:
        return {"Authorization": f"Bearer {self._token}"} if self._token else {}

    async def _get(self, path: str) -> Any:
        try:
            async with self._session.get(
                f"{self.url}{path}", headers=self._headers, timeout=aiohttp.ClientTimeout(total=15)
            ) as r:
                if r.status == 401:
                    raise BridgeAuthError("Wrong token")
                r.raise_for_status()
                return await r.json(content_type=None)
        except BridgeAuthError:
            raise
        except (aiohttp.ClientError, asyncio.TimeoutError) as err:
            raise BridgeError(str(err)) from err

    async def info(self) -> dict[str, Any]:
        return await self._get("/api/info")

    async def entities(self) -> dict[str, Any]:
        return await self._get("/api/entities")

    async def state(self) -> list[dict[str, Any]]:
        return await self._get("/api/state")

    async def usage(self) -> dict[str, Any]:
        return await self._get("/api/usage")

    async def command(self, vin: str, name: str, payload: str) -> None:
        try:
            async with self._session.post(
                f"{self.url}/api/vehicles/{vin}/cmd/{name}",
                data=payload.encode(),
                headers={**self._headers, "Content-Type": "text/plain"},
                timeout=aiohttp.ClientTimeout(total=15),
            ) as r:
                if r.status == 401:
                    raise BridgeAuthError("Wrong token")
                if r.status >= 400:
                    raise BridgeError((await r.text())[:300])
        except (aiohttp.ClientError, asyncio.TimeoutError) as err:
            raise BridgeError(str(err)) from err

    async def events(self) -> AsyncIterator[dict[str, Any]]:
        """Server-Sent Events. Ends when the connection closes."""
        async with self._session.get(
            f"{self.url}/api/events",
            headers={**self._headers, "Accept": "text/event-stream"},
            timeout=aiohttp.ClientTimeout(total=None, sock_connect=15, sock_read=60),  # the bridge pings every 15 s
        ) as r:
            if r.status == 401:
                raise BridgeAuthError("Wrong token")
            r.raise_for_status()
            data: list[str] = []
            async for raw in r.content:
                line = raw.decode("utf-8").rstrip("\r\n")
                if line.startswith("data:"):
                    data.append(line[5:].lstrip())
                elif line == "" and data:
                    try:
                        yield json.loads("\n".join(data))
                    except ValueError:
                        pass
                    data = []
