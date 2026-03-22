from __future__ import annotations

import argparse
import json
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Iterable, Optional

from . import __version__
from .client import ClientError, UnityClient
from .discovery import ACTIVE_INSTANCE_MAX_AGE_SECONDS, DiscoveryError, InstanceInfo, resolve_instance


DEFAULT_STATUS_TIMEOUT_MS = 180000
DEFAULT_VERIFY_TIMEOUT_MS = 180000
DEFAULT_PLAYTEST_TIMEOUT_MS = 120000
DEFAULT_PLAYTEST_DURATION_SECONDS = 5.0
STATUS_RETRY_COUNT = 2
STATUS_RETRY_DELAY_SECONDS = 0.5
STATUS_FILE_MAX_AGE_SECONDS = 10.0
STATUS_FILE_RELOADING_MAX_AGE_SECONDS = 180.0
WAIT_LOOP_INTERVAL_SECONDS = 1.0
PLAYTEST_POLL_INTERVAL_SECONDS = 0.5
CONNECTOR_RESOLVE_TIMEOUT_SECONDS = 20.0
CONNECTOR_RESOLVE_RETRY_SECONDS = 0.5


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
    deadline = time.monotonic() + CONNECTOR_RESOLVE_TIMEOUT_SECONDS
    last_error: Optional[Exception] = None

    while True:
        try:
            instance = resolve_instance(project=args.project, max_age_seconds=ACTIVE_INSTANCE_MAX_AGE_SECONDS)
            client = UnityClient(instance.url)
            client.health(retries=STATUS_RETRY_COUNT, retry_delay=STATUS_RETRY_DELAY_SECONDS)
            return client
        except (DiscoveryError, ClientError) as exc:
            last_error = exc

        if time.monotonic() >= deadline:
            raise last_error if last_error is not None else DiscoveryError("No active Cubix Unity instance was found.")

        time.sleep(CONNECTOR_RESOLVE_RETRY_SECONDS)


def load_recent_status_snapshot(instance: InstanceInfo) -> Optional[Dict[str, Any]]:
    if not instance.status_file:
        return None

    path = Path(instance.status_file)
    if not path.exists():
        return None

    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None

    updated_at_utc = payload.get("lastUpdatedUtc")
    if not isinstance(updated_at_utc, str):
        return None

    try:
        updated_at = datetime.fromisoformat(updated_at_utc.replace("Z", "+00:00")).astimezone(timezone.utc)
    except ValueError:
        return None

    age_seconds = (datetime.now(timezone.utc) - updated_at).total_seconds()
    max_age_seconds = (
        STATUS_FILE_RELOADING_MAX_AGE_SECONDS
        if is_reloading_snapshot(payload)
        else STATUS_FILE_MAX_AGE_SECONDS
    )
    if age_seconds > max_age_seconds:
        return None

    return payload if isinstance(payload, dict) else None


def fetch_status_snapshot(args: argparse.Namespace, allow_file_fallback: bool = False) -> Dict[str, Any]:
    last_error: Optional[ClientError] = None

    for attempt in range(2):
        instance = resolve_instance(project=args.project, max_age_seconds=ACTIVE_INSTANCE_MAX_AGE_SECONDS)
        client = UnityClient(instance.url)

        try:
            return client.status(retries=STATUS_RETRY_COUNT, retry_delay=STATUS_RETRY_DELAY_SECONDS)["data"]
        except ClientError as exc:
            last_error = exc
            if allow_file_fallback:
                snapshot = load_recent_status_snapshot(instance)
                if snapshot is not None:
                    return snapshot

            if attempt == 0:
                time.sleep(CONNECTOR_RESOLVE_RETRY_SECONDS)

    raise last_error if last_error is not None else ClientError("Unity status request failed.")


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
    deadline = time.monotonic() + timeout_seconds + 15.0
    last_state: Dict[str, Any] = {"id": job_id, "state": "pending"}
    last_error: Optional[str] = None

    while time.monotonic() < deadline:
        try:
            verify = fetch_status_snapshot(args, allow_file_fallback=True).get("verify")
            if verify and verify.get("id") == job_id:
                last_state = verify
                if verify.get("success") is not None:
                    return verify
        except (DiscoveryError, ClientError) as exc:
            last_error = str(exc)

        time.sleep(WAIT_LOOP_INTERVAL_SECONDS)

    last_state.setdefault("success", False)
    last_state.setdefault(
        "message",
        "Timed out while waiting for verify status." + (f" Last connector error: {last_error}" if last_error else ""),
    )
    return last_state


def find_latest_script(client: UnityClient) -> Optional[str]:
    try:
        project_path = Path(client.status(retries=STATUS_RETRY_COUNT, retry_delay=STATUS_RETRY_DELAY_SECONDS)["data"]["projectPath"])
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


def command_data(client: UnityClient, command: str, params: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
    data = client.command(command, params or {}).get("data")
    return data if isinstance(data, dict) else {}


def wait_for_play_mode_state(
    client: UnityClient,
    desired_is_playing: bool,
    timeout_seconds: float,
) -> Dict[str, Any]:
    deadline = time.monotonic() + max(timeout_seconds, 1.0)
    last_state: Dict[str, Any] = {}

    while time.monotonic() < deadline:
        state = command_data(client, "editor.state")
        last_state = state
        if bool(state.get("isPlaying")) == desired_is_playing:
            return state

        time.sleep(PLAYTEST_POLL_INTERVAL_SECONDS)

    return last_state


def read_console_entries(
    client: UnityClient,
    level: Optional[str] = None,
    limit: int = 20,
    source: str = "editor",
) -> list[Dict[str, Any]]:
    payload: Dict[str, Any] = {"limit": limit, "source": source}
    if level:
        payload["level"] = level

    data = command_data(client, "console.read", payload)
    entries = data.get("entries")
    if not isinstance(entries, list):
        return []

    return [entry for entry in entries if isinstance(entry, dict)]


def handle_playtest(args: argparse.Namespace) -> int:
    try:
        ready_snapshot = wait_for_ready(args, args.ready_timeout_ms)
        if not ready_snapshot.get("ready"):
            output(
                {
                    "success": False,
                    "message": ready_snapshot.get("message", "Unity is not ready for playtest."),
                    "status": ready_snapshot,
                },
                args.json,
            )
            return 2

        client = build_client(args)
        overall_deadline = time.monotonic() + max(args.timeout_ms, 1000) / 1000.0

        before_state = command_data(client, "editor.state")
        restarted_from_playing = bool(before_state.get("isPlaying"))
        if restarted_from_playing:
            command_data(client, "editor.stop")
            before_state = wait_for_play_mode_state(client, desired_is_playing=False, timeout_seconds=5.0)
            if before_state.get("isPlaying"):
                output(
                    {
                        "success": False,
                        "stopReason": "stop_before_start_failed",
                        "message": "Could not stop the current play mode session before starting playtest.",
                        "editor": {"before": before_state},
                    },
                    args.json,
                )
                return 2

        console_cleared = False
        if not args.preserve_console:
            command_data(client, "console.clear")
            console_cleared = True

        started_state = command_data(client, "editor.play")
        entered_state = wait_for_play_mode_state(
            client,
            desired_is_playing=True,
            timeout_seconds=max(min(overall_deadline - time.monotonic(), 15.0), 1.0),
        )
        if not entered_state.get("isPlaying"):
            output(
                {
                    "success": False,
                    "stopReason": "play_mode_start_failed",
                    "message": "Unity did not enter play mode before the playtest timeout.",
                    "editor": {
                        "before": before_state,
                        "requestedStart": started_state,
                        "entered": entered_state,
                    },
                    "console": {
                        "cleared": console_cleared,
                        "errors": read_console_entries(client, level="error", limit=args.error_limit),
                    },
                },
                args.json,
            )
            return 2

        runtime_start = command_data(client, "runtime.state")
        monitor_started_at = time.monotonic()
        requested_end_at = monitor_started_at + max(args.duration_seconds, 0.0)

        last_runtime_state = runtime_start
        playtest_errors: list[Dict[str, Any]] = []
        success = True
        stop_reason = "duration_elapsed"

        while time.monotonic() < requested_end_at:
            if time.monotonic() >= overall_deadline:
                success = False
                stop_reason = "timeout"
                break

            last_runtime_state = command_data(client, "runtime.state")
            if not last_runtime_state.get("isPlaying"):
                success = False
                stop_reason = "play_mode_exited"
                break

            if not args.ignore_console_errors:
                playtest_errors = read_console_entries(client, level="error", limit=args.error_limit)
                if playtest_errors:
                    success = False
                    stop_reason = "console_error"
                    break

            time.sleep(PLAYTEST_POLL_INTERVAL_SECONDS)

        final_state = command_data(client, "editor.state")
        if final_state.get("isPlaying"):
            command_data(client, "editor.stop")
            final_state = wait_for_play_mode_state(client, desired_is_playing=False, timeout_seconds=5.0)
            if final_state.get("isPlaying"):
                success = False
                stop_reason = "stop_failed"

        duration_seconds = max(time.monotonic() - monitor_started_at, 0.0)
        start_frame = runtime_start.get("frameCount")
        end_frame = last_runtime_state.get("frameCount")
        frames_elapsed = (
            end_frame - start_frame
            if isinstance(start_frame, int) and isinstance(end_frame, int)
            else None
        )
        start_realtime = runtime_start.get("realtimeSinceStartup")
        end_realtime = last_runtime_state.get("realtimeSinceStartup")
        realtime_elapsed = (
            end_realtime - start_realtime
            if isinstance(start_realtime, (int, float)) and isinstance(end_realtime, (int, float))
            else None
        )

        result = {
            "success": success,
            "stopReason": stop_reason,
            "message": (
                "Playtest completed without runtime console errors."
                if success
                else "Playtest detected an issue during play mode or could not restore the editor state."
            ),
            "durationSecondsRequested": args.duration_seconds,
            "durationSecondsElapsed": round(duration_seconds, 3),
            "runtime": {
                "start": runtime_start,
                "end": last_runtime_state,
                "framesElapsed": frames_elapsed,
                "realtimeElapsed": round(realtime_elapsed, 3) if isinstance(realtime_elapsed, (int, float)) else None,
            },
            "editor": {
                "before": before_state,
                "entered": entered_state,
                "final": final_state,
                "restartedFromPlaying": restarted_from_playing,
            },
            "console": {
                "cleared": console_cleared,
                "errorCount": len(playtest_errors),
                "errors": playtest_errors,
            },
        }
        output(result, args.json)
        return 0 if success else 2
    except (DiscoveryError, ClientError) as exc:
        output({"success": False, "message": str(exc)}, args.json)
        return 1


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
        snapshot = wait_for_ready(args, args.timeout_ms) if args.wait_ready else fetch_status_snapshot(args, allow_file_fallback=True)
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
    if is_reloading_snapshot(snapshot):
        return False

    return bool(snapshot.get("ready")) or (
        not editor.get("isCompiling", False)
        and not editor.get("isUpdating", False)
        and not verify_pending
    )


def is_reloading_snapshot(snapshot: Dict[str, Any]) -> bool:
    connection = snapshot.get("connection")
    return bool(snapshot.get("reloading")) or (
        isinstance(connection, dict) and bool(connection.get("reloading"))
    )


def wait_for_ready(args: argparse.Namespace, timeout_ms: int) -> Dict[str, Any]:
    deadline = time.monotonic() + max(timeout_ms, 1000) / 1000.0
    last_snapshot: Dict[str, Any] = {}
    last_error: Optional[str] = None

    while time.monotonic() < deadline:
        try:
            snapshot = fetch_status_snapshot(args, allow_file_fallback=True)
            last_snapshot = snapshot
            if is_ready_snapshot(snapshot):
                snapshot["ready"] = True
                return snapshot
        except (DiscoveryError, ClientError) as exc:
            last_error = str(exc)

        time.sleep(WAIT_LOOP_INTERVAL_SECONDS)

    last_snapshot.setdefault("ready", False)
    last_snapshot.setdefault(
        "message",
        "Timed out while waiting for Unity to become ready." + (f" Last connector error: {last_error}" if last_error else ""),
    )
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
    status.add_argument("--timeout-ms", dest="timeout_ms", type=int, default=DEFAULT_STATUS_TIMEOUT_MS)
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
    verify.add_argument("--timeout-ms", dest="timeout_ms", type=int, default=DEFAULT_VERIFY_TIMEOUT_MS)
    verify.set_defaults(handler=handle_verify)

    playtest = subparsers.add_parser("playtest", help="Run a simple Unity editor play mode smoke test.")
    playtest.add_argument("--duration-seconds", dest="duration_seconds", type=float, default=DEFAULT_PLAYTEST_DURATION_SECONDS)
    playtest.add_argument("--timeout-ms", dest="timeout_ms", type=int, default=DEFAULT_PLAYTEST_TIMEOUT_MS)
    playtest.add_argument("--ready-timeout-ms", dest="ready_timeout_ms", type=int, default=DEFAULT_STATUS_TIMEOUT_MS)
    playtest.add_argument("--error-limit", dest="error_limit", type=int, default=20)
    playtest.add_argument("--preserve-console", action="store_true", help="Do not clear the Unity console before starting play mode.")
    playtest.add_argument("--ignore-console-errors", action="store_true", help="Do not fail the playtest when editor console errors appear during play mode.")
    playtest.set_defaults(handler=handle_playtest)

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
