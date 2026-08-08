from __future__ import annotations

import json
import os
import time
from dataclasses import dataclass, replace
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Tuple


class DiscoveryError(RuntimeError):
    """Raised when no Unity instance can be selected."""


ACTIVE_INSTANCE_MAX_AGE_SECONDS = 15.0
RELOADING_INSTANCE_MAX_AGE_SECONDS = 180.0
STABLE_READ_RETRY_DELAYS_SECONDS = (0.005, 0.015, 0.03)
EPOCH_UTC = datetime.fromtimestamp(0, tz=timezone.utc)


class _UnstableFileReadError(OSError):
    pass


@dataclass(frozen=True)
class InstanceInfo:
    project_name: str
    project_path: str
    project_hash: str
    port: int
    url: str
    updated_at_utc: Optional[str]
    status_file: Optional[str]
    file_modified_at_utc: Optional[datetime] = None

    @property
    def updated_at(self) -> datetime:
        parsed = parse_utc_timestamp(self.updated_at_utc)
        if parsed is not None:
            return parsed
        if self.file_modified_at_utc is not None:
            return self.file_modified_at_utc
        return EPOCH_UTC


def instances_root() -> Path:
    return Path.home() / ".cubic-cli" / "instances"


def parse_utc_timestamp(value: Any) -> Optional[datetime]:
    if not isinstance(value, str) or not value.strip():
        return None

    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None

    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return parsed.astimezone(timezone.utc)


def _file_signature(stat_result: os.stat_result) -> Tuple[int, int, int, int]:
    return (
        stat_result.st_dev,
        stat_result.st_ino,
        stat_result.st_size,
        stat_result.st_mtime_ns,
    )


def _read_json_once(path: Path) -> Tuple[Dict[str, Any], datetime]:
    with path.open("rb") as stream:
        before = os.fstat(stream.fileno())
        raw = stream.read()
        after = os.fstat(stream.fileno())

    if not raw:
        raise _UnstableFileReadError("JSON file was empty while being read.")
    if _file_signature(before) != _file_signature(after) or len(raw) != after.st_size:
        raise _UnstableFileReadError("JSON file changed while being read.")

    payload = json.loads(raw.decode("utf-8-sig"))
    if not isinstance(payload, dict):
        raise json.JSONDecodeError("Expected a JSON object.", raw.decode("utf-8-sig"), 0)

    modified_at = datetime.fromtimestamp(after.st_mtime, tz=timezone.utc)
    return payload, modified_at


def read_stable_json(path: Path) -> Optional[Tuple[Dict[str, Any], datetime]]:
    for attempt in range(len(STABLE_READ_RETRY_DELAYS_SECONDS) + 1):
        try:
            return _read_json_once(path)
        except (OSError, UnicodeDecodeError, json.JSONDecodeError):
            if attempt >= len(STABLE_READ_RETRY_DELAYS_SECONDS):
                return None
            time.sleep(STABLE_READ_RETRY_DELAYS_SECONDS[attempt])

    return None


def load_instances() -> List[InstanceInfo]:
    root = instances_root()
    if not root.exists():
        return []

    instances: List[InstanceInfo] = []
    for path in root.glob("*.json"):
        loaded = read_stable_json(path)
        if loaded is None:
            continue
        payload, modified_at = loaded

        port = payload.get("port")
        url = payload.get("url")
        project_path = payload.get("projectPath")
        if not port or not url or not project_path:
            continue

        try:
            resolved_port = int(port)
        except (TypeError, ValueError):
            continue

        instances.append(InstanceInfo(
            project_name=payload.get("projectName") or Path(project_path).name,
            project_path=str(Path(project_path).resolve()),
            project_hash=payload.get("projectHash") or path.stem,
            port=resolved_port,
            url=str(url).rstrip("/"),
            updated_at_utc=payload.get("updatedAtUtc"),
            status_file=payload.get("statusFile"),
            file_modified_at_utc=modified_at,
        ))

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
    loaded = load_instance_status_with_mtime(instance)
    return loaded[0] if loaded is not None else None


def load_instance_status_with_mtime(
    instance: InstanceInfo,
) -> Optional[Tuple[Dict[str, Any], datetime]]:
    if not instance.status_file:
        return None

    path = Path(instance.status_file)
    return read_stable_json(path)


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


def parse_status_updated_at(
    payload: Dict[str, Any],
    file_modified_at_utc: Optional[datetime] = None,
) -> datetime:
    for key in ("updatedAtUtc", "lastUpdatedUtc", "lastUpdate"):
        parsed = parse_utc_timestamp(payload.get(key))
        if parsed is not None:
            return parsed

    return file_modified_at_utc or EPOCH_UTC


def get_instance_last_seen_at(instance: InstanceInfo) -> datetime:
    last_seen_at = instance.updated_at
    loaded = load_instance_status_with_mtime(instance)
    if loaded is not None:
        payload, modified_at = loaded
        last_seen_at = max(last_seen_at, parse_status_updated_at(payload, modified_at))
    return last_seen_at


def is_reloading_status(payload: Dict[str, Any]) -> bool:
    connection = payload.get("connection")
    return bool(payload.get("reloading")) or (
        isinstance(connection, dict) and bool(connection.get("reloading"))
    )


def is_reloading_instance(
    instance: InstanceInfo,
    max_age_seconds: float = RELOADING_INSTANCE_MAX_AGE_SECONDS,
) -> bool:
    loaded = load_instance_status_with_mtime(instance)
    if loaded is None:
        return False
    payload, modified_at = loaded
    if not is_reloading_status(payload):
        return False

    age_seconds = (
        datetime.now(timezone.utc) - parse_status_updated_at(payload, modified_at)
    ).total_seconds()
    return age_seconds <= max(max_age_seconds, 0.0)


def resolve_instance(
    cwd: Optional[str] = None,
    project: Optional[str] = None,
    max_age_seconds: Optional[float] = None,
) -> InstanceInfo:
    current_dir = Path(cwd or os.getcwd()).resolve()
    instances = load_instances()
    if not instances:
        raise DiscoveryError("No Cubic Unity instance files were found under ~/.cubic-cli/instances.")

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
        f"No active Cubic Unity instance is currently advertising{target}. The connector may be reloading."
    )
