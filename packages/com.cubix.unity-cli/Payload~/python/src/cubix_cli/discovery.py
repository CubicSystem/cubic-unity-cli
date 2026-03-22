from __future__ import annotations

import json
import os
from dataclasses import dataclass, replace
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional


class DiscoveryError(RuntimeError):
    """Raised when no Unity instance can be selected."""


ACTIVE_INSTANCE_MAX_AGE_SECONDS = 15.0
RELOADING_INSTANCE_MAX_AGE_SECONDS = 180.0


@dataclass(frozen=True)
class InstanceInfo:
    project_name: str
    project_path: str
    project_hash: str
    port: int
    url: str
    updated_at_utc: Optional[str]
    status_file: Optional[str]

    @property
    def updated_at(self) -> datetime:
        if not self.updated_at_utc:
            return datetime.fromtimestamp(0, tz=timezone.utc)
        try:
            return datetime.fromisoformat(self.updated_at_utc.replace("Z", "+00:00")).astimezone(timezone.utc)
        except ValueError:
            return datetime.fromtimestamp(0, tz=timezone.utc)


def instances_root() -> Path:
    return Path.home() / ".cubix-cli" / "instances"


def load_instances() -> List[InstanceInfo]:
    root = instances_root()
    if not root.exists():
        return []

    instances: List[InstanceInfo] = []
    for path in root.glob("*.json"):
        try:
            payload = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue

        port = payload.get("port")
        url = payload.get("url")
        project_path = payload.get("projectPath")
        if not port or not url or not project_path:
            continue

        instances.append(
            InstanceInfo(
                project_name=payload.get("projectName") or Path(project_path).name,
                project_path=str(Path(project_path).resolve()),
                project_hash=payload.get("projectHash") or path.stem,
                port=int(port),
                url=str(url).rstrip("/"),
                updated_at_utc=payload.get("updatedAtUtc"),
                status_file=payload.get("statusFile"),
            )
        )

    return instances


def rank_instances(instances: Iterable[InstanceInfo], cwd: Path, project: Optional[str]) -> List[InstanceInfo]:
    project_path = Path(project).resolve() if project else None

    return sorted(instances, key=lambda instance: score_instance(instance, cwd, project_path))


def score_instance(instance: InstanceInfo, cwd: Path, project_path: Optional[Path]) -> tuple[int, int, float]:
    instance_path = Path(instance.project_path)
    explicit = 0
    proximity = 0

    if project_path and instance_path == project_path:
        explicit = 0
    elif project_path:
        explicit = 1
    elif cwd == instance_path or instance_path in cwd.parents:
        explicit = 0
        proximity = len(instance_path.parts)
    else:
        explicit = 2

    return (explicit, -proximity, -get_instance_last_seen_at(instance).timestamp())


def is_active_instance(instance: InstanceInfo, max_age_seconds: float = ACTIVE_INSTANCE_MAX_AGE_SECONDS) -> bool:
    age_seconds = (datetime.now(timezone.utc) - get_instance_last_seen_at(instance)).total_seconds()
    return age_seconds <= max(max_age_seconds, 0.0)


def load_instance_status(instance: InstanceInfo) -> Optional[Dict[str, Any]]:
    if not instance.status_file:
        return None

    path = Path(instance.status_file)
    if not path.exists():
        return None

    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None

    return payload if isinstance(payload, dict) else None


def merge_instance_endpoint_from_status(instance: InstanceInfo) -> InstanceInfo:
    payload = load_instance_status(instance)
    if payload is None:
        return instance

    port = payload.get("port")
    url = payload.get("url")
    if not port or not isinstance(url, str) or not url.strip():
        return instance

    try:
        resolved_port = int(port)
    except (TypeError, ValueError):
        return instance

    return replace(instance, port=resolved_port, url=url.rstrip("/"))


def parse_status_updated_at(payload: Dict[str, Any]) -> datetime:
    updated_at_utc = payload.get("lastUpdatedUtc") or payload.get("lastUpdate")
    if not isinstance(updated_at_utc, str):
        return datetime.fromtimestamp(0, tz=timezone.utc)

    try:
        return datetime.fromisoformat(updated_at_utc.replace("Z", "+00:00")).astimezone(timezone.utc)
    except ValueError:
        return datetime.fromtimestamp(0, tz=timezone.utc)


def get_instance_last_seen_at(instance: InstanceInfo) -> datetime:
    payload = load_instance_status(instance)
    if payload is not None:
        updated_at = parse_status_updated_at(payload)
        if updated_at.timestamp() > 0:
            return updated_at

    return instance.updated_at


def is_reloading_status(payload: Dict[str, Any]) -> bool:
    connection = payload.get("connection")
    return bool(payload.get("reloading")) or (
        isinstance(connection, dict) and bool(connection.get("reloading"))
    )


def is_reloading_instance(
    instance: InstanceInfo,
    max_age_seconds: float = RELOADING_INSTANCE_MAX_AGE_SECONDS,
) -> bool:
    payload = load_instance_status(instance)
    if payload is None or not is_reloading_status(payload):
        return False

    age_seconds = (datetime.now(timezone.utc) - parse_status_updated_at(payload)).total_seconds()
    return age_seconds <= max(max_age_seconds, 0.0)


def resolve_instance(
    cwd: Optional[str] = None,
    project: Optional[str] = None,
    max_age_seconds: Optional[float] = None,
) -> InstanceInfo:
    current_dir = Path(cwd or os.getcwd()).resolve()
    instances = load_instances()
    if not instances:
        raise DiscoveryError("No Cubix Unity instance files were found under ~/.cubix-cli/instances.")

    project_path = Path(project).resolve() if project else None
    if project_path:
        instances = [instance for instance in instances if Path(instance.project_path) == project_path]
        if not instances:
            raise DiscoveryError(f"No running Unity instance matched project path '{project_path}'.")

    ranked = rank_instances(instances, current_dir, project)
    if max_age_seconds is None:
        return merge_instance_endpoint_from_status(ranked[0])

    best_score = score_instance(ranked[0], current_dir, project_path)[:2]
    preferred = [
        instance
        for instance in ranked
        if score_instance(instance, current_dir, project_path)[:2] == best_score
    ]

    for instance in preferred:
        if is_active_instance(instance, max_age_seconds):
            return merge_instance_endpoint_from_status(instance)

    for instance in preferred:
        if is_reloading_instance(instance):
            return merge_instance_endpoint_from_status(instance)

    target = f" for project '{project_path}'" if project_path else ""
    raise DiscoveryError(
        f"No active Cubix Unity instance is currently advertising{target}. The connector may be reloading."
    )
