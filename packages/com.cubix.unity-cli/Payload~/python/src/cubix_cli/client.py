from __future__ import annotations

import json
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from typing import Any, Dict, Optional


class ClientError(RuntimeError):
    """Raised when the Unity connector request fails."""


@dataclass
class UnityClient:
    base_url: str
    timeout: float = 10.0

    def health(self, retries: int = 0, retry_delay: float = 0.5) -> Dict[str, Any]:
        return self._request("GET", "/health", retries=retries, retry_delay=retry_delay)

    def status(self, retries: int = 0, retry_delay: float = 0.5) -> Dict[str, Any]:
        return self._request("GET", "/status", retries=retries, retry_delay=retry_delay)

    def command(self, command: str, params: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
        return self._request("POST", "/command", {"command": command, "params": params or {}})

    def _request(
        self,
        method: str,
        path: str,
        payload: Optional[Dict[str, Any]] = None,
        retries: int = 0,
        retry_delay: float = 0.5,
    ) -> Dict[str, Any]:
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

        attempt = 0
        while True:
            try:
                with urllib.request.urlopen(request, timeout=self.timeout) as response:
                    body = response.read().decode("utf-8")
            except urllib.error.HTTPError as exc:
                body = exc.read().decode("utf-8", errors="replace")
                raise ClientError(body or f"HTTP {exc.code} returned from Unity connector.") from exc
            except urllib.error.URLError as exc:
                error = ClientError(str(exc.reason))
            except OSError as exc:
                error = ClientError(str(exc))
            else:
                try:
                    decoded = json.loads(body)
                except json.JSONDecodeError as exc:
                    error = ClientError(f"Connector returned invalid JSON: {body}")
                else:
                    if not decoded.get("success", False):
                        raise ClientError(decoded.get("message", "Connector command failed."))
                    return decoded

            if attempt >= retries:
                raise error

            attempt += 1
            time.sleep(max(retry_delay, 0.0))
