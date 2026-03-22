from __future__ import annotations

import argparse
import json
import time
from pathlib import Path
from typing import Any, Dict, Iterable, Optional

from . import __version__
from .client import ClientError, UnityClient
from .discovery import DiscoveryError, resolve_instance


def parse_bool(value: str) -> bool:
    lowered = value.strip().lower()
    if lowered in {"1", "true", "yes", "on"}:
        return True
    if lowered in {"0", "false", "no", "off"}:
        return False
    raise argparse.ArgumentTypeError(f"Invalid boolean value: {value}")


def parse_value(value: str) -> Any:
    try:
        return json.loads(value)
    except json.JSONDecodeError:
        return value


def parse_vector(value: Optional[str]) -> Optional[str]:
    if value is None:
        return None
    return value.strip()


def output(payload: Any, as_json: bool) -> None:
    if as_json:
        print(json.dumps(payload, indent=2, ensure_ascii=False))
        return

    if isinstance(payload, (dict, list)):
        print(json.dumps(payload, indent=2, ensure_ascii=False))
        return

    print(payload)


def build_client(args: argparse.Namespace) -> UnityClient:
    instance = resolve_instance(project=args.project)
    client = UnityClient(instance.url)
    client.health()
    return client


def connector_params_from_args(args: argparse.Namespace) -> Dict[str, Any]:
    params: Dict[str, Any] = {}
    for key, value in vars(args).items():
        if key in {
            "command_group",
            "command_action",
            "handler",
            "project",
            "json",
            "target_command",
            "params",
            "file",
            "continue_on_error",
            "wait_ready",
        }:
            continue
        if value is None:
            continue
        params[key] = value
    return params


def maybe_add_transform_args(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--position")
    parser.add_argument("--local-position", dest="localPosition")
    parser.add_argument("--local-scale", dest="localScale")
    parser.add_argument("--rotation-euler", dest="rotationEuler")
    parser.add_argument("--local-rotation-euler", dest="localRotationEuler")


def load_params_payload(args: argparse.Namespace) -> Dict[str, Any]:
    inline = getattr(args, "params", None)
    file_path = getattr(args, "file", None)

    if inline is None and file_path is None:
        return {}

    try:
        if inline is not None:
            payload = json.loads(inline)
        else:
            payload = json.loads(Path(file_path).read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ClientError(str(exc)) from exc

    if not isinstance(payload, dict):
        raise ClientError("Command params must be a JSON object.")

    return payload


def load_batch_payload(path: str) -> Dict[str, Any]:
    try:
        payload = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ClientError(str(exc)) from exc

    if not isinstance(payload, dict):
        raise ClientError("Batch payload must be a JSON object.")

    calls = payload.get("calls")
    if not isinstance(calls, list):
        raise ClientError("Batch payload must contain a 'calls' array.")

    for call in calls:
        if not isinstance(call, dict) or not isinstance(call.get("command"), str):
            raise ClientError("Each batch call must be an object with a string 'command'.")
        if "params" in call and not isinstance(call["params"], dict):
            raise ClientError("Each batch call 'params' value must be a JSON object.")

    return payload


def handle_verify(args: argparse.Namespace) -> int:
    try:
        client = build_client(args)
        target_path = args.path
        if args.all:
            target_path = None
            mode = "refresh"
        elif target_path:
            mode = "reimport" if Path(target_path).exists() else "refresh"
        else:
            target_path = find_latest_script(client)
            mode = "reimport" if target_path else "refresh"

        response = client.command(
            "verify.run",
            {
                "path": target_path,
                "mode": mode,
                "timeoutMs": args.timeout_ms,
            },
        )
        data = response["data"]
        final_state = wait_for_verify(args, data["id"], args.timeout_ms / 1000.0)
        success = bool(final_state.get("success"))
        output(final_state, args.json)
        return 0 if success else 2
    except (DiscoveryError, ClientError) as exc:
        output({"success": False, "message": str(exc)}, args.json)
        return 1


def wait_for_verify(args: argparse.Namespace, job_id: str, timeout_seconds: float) -> Dict[str, Any]:
    deadline = time.monotonic() + timeout_seconds + 5.0
    last_state: Dict[str, Any] = {"id": job_id, "state": "pending"}

    while time.monotonic() < deadline:
        try:
            client = build_client(args)
            verify = client.status()["data"].get("verify")
            if verify and verify.get("id") == job_id:
                last_state = verify
                if verify.get("success") is not None:
                    return verify
        except (DiscoveryError, ClientError):
            pass

        time.sleep(1.0)

    last_state.setdefault("success", False)
    last_state.setdefault("message", "Timed out while waiting for verify status.")
    return last_state


def find_latest_script(client: UnityClient) -> Optional[str]:
    try:
        project_path = Path(client.status()["data"]["projectPath"])
    except Exception:
        return None

    assets = project_path / "Assets"
    if not assets.exists():
        return None

    scripts = sorted(assets.rglob("*.cs"), key=lambda item: item.stat().st_mtime, reverse=True)
    if not scripts:
        return None

    latest = scripts[0]
    return latest.relative_to(project_path).as_posix()


def handle_connector_command(args: argparse.Namespace) -> int:
    try:
        client = build_client(args)
        command = f"{args.command_group}.{args.command_action}"
        payload = connector_params_from_args(args)
        response = client.command(command, payload)
        output(response["data"], args.json)
        return 0
    except (DiscoveryError, ClientError) as exc:
        output({"success": False, "message": str(exc)}, args.json)
        return 1


def handle_exec(args: argparse.Namespace) -> int:
    try:
        client = build_client(args)
        code = args.code
        if args.file:
            code = Path(args.file).read_text(encoding="utf-8")
        response = client.command(
            "exec.csharp",
            {
                "code": code,
                "usings": args.usings or [],
            },
        )
        output(response["data"], args.json)
        return 0
    except (OSError, DiscoveryError, ClientError) as exc:
        output({"success": False, "message": str(exc)}, args.json)
        return 1


def handle_status(args: argparse.Namespace) -> int:
    try:
        snapshot = wait_for_ready(args, args.timeout_ms) if args.wait_ready else build_client(args).status()["data"]
        output(snapshot, args.json)
        return 0 if snapshot.get("ready") else 2
    except (DiscoveryError, ClientError) as exc:
        output({"success": False, "message": str(exc)}, args.json)
        return 1


def handle_list(args: argparse.Namespace) -> int:
    try:
        response = build_client(args).command(
            "commands.list",
            {
                "group": args.group,
                "tag": args.tag,
                "search": args.search,
                "includeUnsafe": args.include_unsafe,
            },
        )
        output(response["data"], args.json)
        return 0
    except (DiscoveryError, ClientError) as exc:
        output({"success": False, "message": str(exc)}, args.json)
        return 1


def handle_help_command(args: argparse.Namespace) -> int:
    try:
        response = build_client(args).command("commands.describe", {"command": args.target_command})
        output(response["data"], args.json)
        return 0
    except (DiscoveryError, ClientError) as exc:
        output({"success": False, "message": str(exc)}, args.json)
        return 1


def handle_call(args: argparse.Namespace) -> int:
    try:
        payload = load_params_payload(args)
        response = build_client(args).command(args.target_command, payload)
        output(response["data"], args.json)
        return 0
    except (DiscoveryError, ClientError) as exc:
        output({"success": False, "message": str(exc)}, args.json)
        return 1


def handle_preflight(args: argparse.Namespace) -> int:
    try:
        payload = load_params_payload(args)
        response = build_client(args).command(
            "commands.preflight",
            {
                "calls": [
                    {
                        "command": args.target_command,
                        "params": payload,
                    }
                ]
            },
        )
        result = response["data"]["results"][0]
        output(result, args.json)
        return 0 if result.get("canExecute") else 2
    except (DiscoveryError, ClientError, KeyError, IndexError, TypeError) as exc:
        output({"success": False, "message": str(exc)}, args.json)
        return 1


def handle_batch(args: argparse.Namespace) -> int:
    try:
        payload = load_batch_payload(args.file)
        response = build_client(args).command(
            "commands.batch",
            {
                "calls": payload["calls"],
                "stopOnError": not args.continue_on_error,
            },
        )
        output(response["data"], args.json)
        failures = [entry for entry in response["data"].get("results", []) if not entry.get("success")]
        return 0 if not failures else 2
    except (DiscoveryError, ClientError, KeyError, TypeError) as exc:
        output({"success": False, "message": str(exc)}, args.json)
        return 1


def is_ready_snapshot(snapshot: Dict[str, Any]) -> bool:
    editor = snapshot.get("editor", {})
    verify = snapshot.get("verify")
    verify_pending = isinstance(verify, dict) and verify.get("success") is None
    return bool(snapshot.get("ready")) or (
        not editor.get("isCompiling", False)
        and not editor.get("isUpdating", False)
        and not verify_pending
    )


def wait_for_ready(args: argparse.Namespace, timeout_ms: int) -> Dict[str, Any]:
    deadline = time.monotonic() + max(timeout_ms, 1000) / 1000.0
    last_snapshot: Dict[str, Any] = {}

    while time.monotonic() < deadline:
        snapshot = build_client(args).status()["data"]
        last_snapshot = snapshot
        if is_ready_snapshot(snapshot):
            snapshot["ready"] = True
            return snapshot
        time.sleep(1.0)

    last_snapshot.setdefault("ready", False)
    last_snapshot.setdefault("message", "Timed out while waiting for Unity to become ready.")
    return last_snapshot


def add_common_arguments(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--project", help="Exact Unity project path to target.")
    parser.add_argument("--json", action="store_true", help="Print JSON output.")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="cubix-cli", description="Cubix Unity CLI")
    parser.add_argument("--version", action="version", version=f"cubix-cli {__version__}")
    add_common_arguments(parser)
    subparsers = parser.add_subparsers(dest="command_group", required=True)

    status = subparsers.add_parser("status", help="Read connector and editor status.")
    status.add_argument("--wait-ready", action="store_true", help="Wait until Unity finishes compiling and updating.")
    status.add_argument("--timeout-ms", dest="timeout_ms", type=int, default=60000)
    status.set_defaults(handler=handle_status)

    list_parser = subparsers.add_parser("list", help="List available Unity commands.")
    list_parser.add_argument("--group")
    list_parser.add_argument("--tag")
    list_parser.add_argument("--search")
    list_parser.add_argument("--include-unsafe", action="store_true", dest="include_unsafe")
    list_parser.set_defaults(handler=handle_list)

    help_parser = subparsers.add_parser("help", help="Describe one Unity command.")
    help_parser.add_argument("target_command")
    help_parser.set_defaults(handler=handle_help_command)

    call_parser = subparsers.add_parser("call", help="Call an arbitrary Unity command with JSON params.")
    call_parser.add_argument("target_command")
    payload_group = call_parser.add_mutually_exclusive_group()
    payload_group.add_argument("--params")
    payload_group.add_argument("--file")
    call_parser.set_defaults(handler=handle_call)

    preflight = subparsers.add_parser("preflight", help="Run preflight checks for one command.")
    preflight.add_argument("target_command")
    preflight_payload = preflight.add_mutually_exclusive_group()
    preflight_payload.add_argument("--params")
    preflight_payload.add_argument("--file")
    preflight.set_defaults(handler=handle_preflight)

    batch = subparsers.add_parser("batch", help="Run a batch JSON file of commands.")
    batch.add_argument("--file", required=True)
    batch.add_argument("--continue-on-error", action="store_true")
    batch.set_defaults(handler=handle_batch)

    verify = subparsers.add_parser("verify", help="Run Unity verify workflow.")
    verify.add_argument("path", nargs="?")
    verify.add_argument("--all", action="store_true", help="Refresh the whole AssetDatabase before verify.")
    verify.add_argument("--timeout-ms", dest="timeout_ms", type=int, default=60000)
    verify.set_defaults(handler=handle_verify)

    editor = subparsers.add_parser("editor", help="Control Unity editor state.")
    editor_sub = editor.add_subparsers(dest="command_action", required=True)
    for action in ("state", "play", "stop"):
        sub = editor_sub.add_parser(action)
        sub.set_defaults(handler=handle_connector_command, command_group="editor", command_action=action)
    pause = editor_sub.add_parser("pause")
    pause.add_argument("--paused", type=parse_bool)
    pause.set_defaults(handler=handle_connector_command, command_group="editor", command_action="pause")

    menu = subparsers.add_parser("menu", help="Validate or execute a Unity menu item.")
    menu.add_argument("menuPath")
    menu.add_argument("--validate-only", dest="validateOnly", action="store_true")
    menu.set_defaults(handler=handle_connector_command, command_group="editor", command_action="menu")

    refresh = subparsers.add_parser("refresh", help="Refresh Unity assets and scripts.")
    refresh.add_argument("--mode", default="scripts", choices=["assets", "scripts", "all"])
    refresh.set_defaults(handler=handle_connector_command, command_group="editor", command_action="refresh")

    console = subparsers.add_parser("console", help="Read or clear Unity console.")
    console_sub = console.add_subparsers(dest="command_action", required=True)
    read = console_sub.add_parser("read")
    read.add_argument("--level")
    read.add_argument("--limit", type=int, default=50)
    read.add_argument("--source", choices=["editor", "compiler"])
    read.set_defaults(handler=handle_connector_command, command_group="console", command_action="read")
    clear = console_sub.add_parser("clear")
    clear.set_defaults(handler=handle_connector_command, command_group="console", command_action="clear")

    scene = subparsers.add_parser("scene", help="Inspect scene state.")
    scene_sub = scene.add_subparsers(dest="command_action", required=True)
    scene_active = scene_sub.add_parser("active")
    scene_active.set_defaults(handler=handle_connector_command, command_group="scene", command_action="active")
    hierarchy = scene_sub.add_parser("hierarchy")
    hierarchy.add_argument("--include-components", dest="includeComponents", action="store_true")
    hierarchy.add_argument("--max-depth", dest="maxDepth", type=int, default=6)
    hierarchy.set_defaults(handler=handle_connector_command, command_group="scene", command_action="hierarchy")
    find = scene_sub.add_parser("find")
    find.add_argument("query", nargs="?")
    find.add_argument("--name")
    find.add_argument("--path")
    find.add_argument("--tag")
    find.add_argument("--include-inactive", dest="includeInactive", action="store_true")
    find.add_argument("--include-components", dest="includeComponents", action="store_true")
    find.set_defaults(handler=handle_connector_command, command_group="scene", command_action="find")

    objects = subparsers.add_parser("object", help="Create and mutate scene objects.")
    object_sub = objects.add_subparsers(dest="command_action", required=True)
    create = object_sub.add_parser("create")
    create.add_argument("name")
    create.add_argument("--parent")
    create.add_argument("--active", type=parse_bool)
    create.add_argument("--select", type=parse_bool, default=True)
    maybe_add_transform_args(create)
    create.set_defaults(handler=handle_connector_command, command_group="object", command_action="create")
    set_active = object_sub.add_parser("set-active")
    set_active.add_argument("target")
    set_active.add_argument("active", type=parse_bool)
    set_active.set_defaults(handler=handle_connector_command, command_group="object", command_action="set-active")
    set_parent = object_sub.add_parser("set-parent")
    set_parent.add_argument("target")
    set_parent.add_argument("parent", nargs="?")
    set_parent.set_defaults(handler=handle_connector_command, command_group="object", command_action="set-parent")
    component_get = object_sub.add_parser("component-get")
    component_get.add_argument("target")
    component_get.add_argument("component")
    component_get.add_argument("--member")
    component_get.add_argument("--include-inactive", dest="includeInactive", action="store_true")
    component_get.set_defaults(handler=handle_connector_command, command_group="object", command_action="component-get")
    component_set = object_sub.add_parser("component-set")
    component_set.add_argument("target")
    component_set.add_argument("component")
    component_set.add_argument("--member")
    component_set.add_argument("--value")
    component_set.add_argument("--values")
    component_set.add_argument("--include-inactive", dest="includeInactive", action="store_true")
    component_set.set_defaults(handler=handle_object_component_set, command_group="object", command_action="component-set")

    prefab = subparsers.add_parser("prefab", help="Instantiate or save prefabs.")
    prefab_sub = prefab.add_subparsers(dest="command_action", required=True)
    instantiate = prefab_sub.add_parser("instantiate")
    instantiate.add_argument("path")
    instantiate.add_argument("--parent")
    instantiate.add_argument("--name")
    maybe_add_transform_args(instantiate)
    instantiate.set_defaults(handler=handle_connector_command, command_group="prefab", command_action="instantiate")
    save = prefab_sub.add_parser("save")
    save.add_argument("target")
    save.add_argument("path")
    save.set_defaults(handler=handle_connector_command, command_group="prefab", command_action="save")
    connect = prefab_sub.add_parser("connect")
    connect.add_argument("target")
    connect.add_argument("path")
    connect.set_defaults(handler=handle_connector_command, command_group="prefab", command_action="connect")

    reserialize = subparsers.add_parser("reserialize", help="Force reserialize explicit asset paths.")
    reserialize.add_argument("paths", nargs="+")
    reserialize.set_defaults(handler=handle_connector_command, command_group="project", command_action="reserialize")

    runtime = subparsers.add_parser("runtime", help="Inspect or mutate runtime state.")
    runtime_sub = runtime.add_subparsers(dest="command_action", required=True)
    runtime_state = runtime_sub.add_parser("state")
    runtime_state.set_defaults(handler=handle_connector_command, command_group="runtime", command_action="state")
    runtime_inspect = runtime_sub.add_parser("inspect")
    runtime_inspect.add_argument("target")
    runtime_inspect.add_argument("--component")
    runtime_inspect.set_defaults(handler=handle_connector_command, command_group="runtime", command_action="inspect")
    runtime_mutate = runtime_sub.add_parser("mutate")
    runtime_mutate.add_argument("target")
    runtime_mutate.add_argument("--component")
    runtime_mutate.add_argument("--member")
    runtime_mutate.add_argument("--value")
    runtime_mutate.add_argument("--values")
    runtime_mutate.add_argument("--active", type=parse_bool)
    maybe_add_transform_args(runtime_mutate)
    runtime_mutate.set_defaults(handler=handle_object_component_set, command_group="runtime", command_action="mutate")

    exec_parser = subparsers.add_parser("exec", help="Run one-off C# snippets.")
    exec_sub = exec_parser.add_subparsers(dest="command_action", required=True)
    csharp = exec_sub.add_parser("csharp")
    code_source = csharp.add_mutually_exclusive_group(required=True)
    code_source.add_argument("--code")
    code_source.add_argument("--file")
    csharp.add_argument("--using", dest="usings", action="append")
    csharp.set_defaults(handler=handle_exec)

    return parser


def handle_object_component_set(args: argparse.Namespace) -> int:
    try:
        client = build_client(args)
        payload = connector_params_from_args(args)
        if "value" in payload:
            payload["value"] = parse_value(payload["value"])
        if "values" in payload:
            payload["values"] = parse_value(payload["values"])
        response = client.command(f"{args.command_group}.{args.command_action}", payload)
        output(response["data"], args.json)
        return 0
    except (DiscoveryError, ClientError, json.JSONDecodeError) as exc:
        output({"success": False, "message": str(exc)}, args.json)
        return 1


def main(argv: Optional[Iterable[str]] = None) -> int:
    parser = build_parser()
    args = parser.parse_args(list(argv) if argv is not None else None)

    for key in ("position", "localPosition", "localScale", "rotationEuler", "localRotationEuler"):
        if hasattr(args, key):
            value = getattr(args, key)
            if value is not None:
                setattr(args, key, parse_vector(value))

    return args.handler(args)


def entrypoint() -> None:
    raise SystemExit(main())


if __name__ == "__main__":
    entrypoint()
