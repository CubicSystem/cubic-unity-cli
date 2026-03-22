from __future__ import annotations

import json
import os
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable, List, Optional


class DiscoveryError(RuntimeError):
    """Raised when no Unity instance can be selected."""


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

    def score(instance: InstanceInfo) -> tuple[int, int, float]:
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

        return (explicit, -proximity, -instance.updated_at.timestamp())

    return sorted(instances, key=score)


def resolve_instance(cwd: Optional[str] = None, project: Optional[str] = None) -> InstanceInfo:
    current_dir = Path(cwd or os.getcwd()).resolve()
    instances = rank_instances(load_instances(), current_dir, project)
    if not instances:
        raise DiscoveryError("No Cubix Unity instance files were found under ~/.cubix-cli/instances.")

    if project:
        project_path = Path(project).resolve()
        exact = [instance for instance in instances if Path(instance.project_path) == project_path]
        if not exact:
            raise DiscoveryError(f"No running Unity instance matched project path '{project_path}'.")
        return exact[0]

    return instances[0]
