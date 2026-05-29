# Cubix Unity CLI

`com.cubicengine.unity-cli` is a Unity editor package that hosts the Cubix Unity CLI connector
and exposes a setup window for connection, CLI installation, and skill installation.

## What The Package Provides

- Loopback HTTP connector bound to `127.0.0.1`
- Discovery and heartbeat files under `%USERPROFILE%\.cubix-cli`
- Serialized main-thread command execution
- Command metadata catalog with filtering and description endpoints
- Batch and preflight command surfaces for advanced automation
- Verify workflow for script compilation checks
- Setup window at `Tools/Cubix/Unity CLI`
- Packaged Python CLI payload installed as `cubix-cli`
- Packaged Codex and Claude Code skills

## Setup Window

Open `Tools/Cubix/Unity CLI` to manage:

- `Connection`
  - `Connect`
  - `Disconnect`
  - `Reconnect`
  - `Refresh Status`
  - `Auto Connect On Load`
- `CLI`
  - Detect Python, pip, pipx, and installed `cubix-cli`
  - `Install CLI`
  - `Update CLI`
  - `Repair CLI`
  - `Uninstall CLI`
  - `Copy Diagnostics`
- `Skills`
  - `Install Codex Skills`
  - `Install Claude Code Skills`
  - `Install All Skills`
  - `Repair Skills`
  - `Remove Codex Skills`
  - `Remove Claude Code Skills`

The CLI installer expects Python 3.10+. If Python is unavailable, the setup window only reports diagnostics and does not try to install Python itself.

The `Connection` section also reports connector readiness and the current command count. The `CLI` diagnostics report advanced capability surfaces such as catalog discovery, dynamic command calls, preflight, and batch.

## HTTP API

- `GET /health`
- `GET /status`
- `POST /command`

Example request:

```json
{
  "command": "verify.run",
  "params": {
    "path": "Assets/Scripts/PlayerController.cs",
    "mode": "reimport",
    "timeoutMs": 60000
  },
  "requestId": "optional-id"
}
```

Example response:

```json
{
  "success": true,
  "message": "Verify started.",
  "data": {
    "id": "job-id",
    "state": "queued"
  }
}
```

`GET /status` includes the persisted verify job and the current connection snapshot.

## Advanced CLI Surface

The packaged `cubix-cli` command includes:

- `status`
- `list`
- `help`
- `call`
- `preflight`
- `batch`
- `menu`
- `refresh`
- `reserialize`

These commands sit on top of the same Unity command catalog exposed by `commands.list` and `commands.describe`.
