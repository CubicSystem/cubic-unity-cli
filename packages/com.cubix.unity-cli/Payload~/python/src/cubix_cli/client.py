from __future__ import annotations

import json
import urllib.error
import urllib.request
from dataclasses import dataclass
from typing import Any, Dict, Optional


class ClientError(RuntimeError):
    """Raised when the Unity connector request fails."""


@dataclass
class UnityClient:
    base_url: str
    timeout: float = 5.0

    def health(self) -> Dict[str, Any]:
        return self._request("GET", "/health")

    def status(self) -> Dict[str, Any]:
        return self._request("GET", "/status")

    def command(self, command: str, params: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
        return self._request("POST", "/command", {"command": command, "params": params or {}})

    def _request(self, method: str, path: str, payload: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
        data = None
        headers = {"Accept": "application/json"}
        if payload is not None:
            data = json.dumps(payload).encode("utf-8")
            headers["Content-Type"] = "application/json"

        request = urllib.request.Request(
            url=f"{self.base_url}{path}",
            data=data,
            method=method,
            headers=headers,
        )

        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:
                body = response.read().decode("utf-8")
        except urllib.error.HTTPError as exc:
            body = exc.read().decode("utf-8", errors="replace")
            raise ClientError(body or f"HTTP {exc.code} returned from Unity connector.") from exc
        except urllib.error.URLError as exc:
            raise ClientError(str(exc.reason)) from exc

        try:
            decoded = json.loads(body)
        except json.JSONDecodeError as exc:
            raise ClientError(f"Connector returned invalid JSON: {body}") from exc

        if not decoded.get("success", False):
            raise ClientError(decoded.get("message", "Connector command failed."))

        return decoded
