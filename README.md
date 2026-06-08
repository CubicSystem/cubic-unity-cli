# Cubic Unity CLI

Repository root package for the `com.cubicengine.unity-cli` Unity package and its packaged payloads.
The Unity package owns the local HTTP connector, the setup window, the Python CLI
payload, and the installable Codex and Claude Code skills.

## Install

Add this Git URL in Unity Package Manager:

```text
https://github.com/CubicSystem/cubic-unity-cli.git
```

Or add it directly to `Packages/manifest.json`:

```json
"com.cubicengine.unity-cli": "https://github.com/CubicSystem/cubic-unity-cli.git"
```

## Layout

- `package.json`
  UPM package manifest. The repository root is the package root.
- `Editor`
  Connection service, command handlers, setup UI, and installer services.
- `Payload~/python`
  Python package payload staged and installed as `cubic-cli` via `pipx`.
- `Payload~/skills`
  Self-contained skill payloads for Codex and Claude Code.

## Install Flow

1. Add `com.cubicengine.unity-cli` to the Unity project's `Packages/manifest.json`.
2. Open the Unity project.
3. Open `Tools/CubicEngine/UnityCli`.
4. Confirm the connection status or use `Connect` / `Reconnect`.
5. Use `Install CLI` to bootstrap `pip`, `pipx`, and the `cubic-cli` command if Python 3.10+ is available.
6. Use `Install Codex Skills`, `Install Claude Code Skills`, or `Install All Skills`.

The package auto-connects on editor load by default. You can disable that from the setup window.

## Setup Window

![Cubic Unity CLI editor](https://raw.githubusercontent.com/CubicSystem/cubic-unity-cli/main/Documentation/Images/cubic-unity-cli-editor.png)

`Tools/CubicEngine/UnityCli` opens the editor window used to operate the package inside Unity.
It is organized around package loading, connector state, CLI installation, and skill installation.

- `Loaded Package`
  Shows the loaded package version and resolved package root. Use `Reload Package Scripts` when
  Unity has stale package scripts or the loaded package version does not match the resolved package.
- `Connection`
  Shows whether the Unity connector is connected, the loopback port and URL, the current project
  hash, registered command count, last error, and `Auto Connect On Load`. Use `Connect` and
  `Disconnect` to control the local connector.
- `CLI`
  Diagnoses Python, `pip`, `pipx`, the expected CLI version, installed `cubic-cli` package version,
  command version, and top-level command test. This section provides install, update, repair,
  uninstall, and diagnostics actions for the local CLI.
- `Skills`
  Detects Codex and Claude Code, shows each app path and skill install target, and reports whether
  `cubic-unity-cli-verify` and `cubic-unity-cli-edit-loop` are installed. Use the section buttons to
  install, repair, or uninstall agent skills.

## Runtime Surface

The editor connector serves loopback HTTP on `127.0.0.1` and keeps discovery files under `%USERPROFILE%\.cubic-cli`.

- `GET /health`
- `GET /status`
- `POST /command`

The command groups exposed by the connector and CLI are:

- `status [--wait-ready]`
- `list [--group NAME] [--tag TAG] [--search TEXT]`
- `help <group.action>`
- `call <group.action> (--params <json> | --file <path>)`
- `preflight <group.action> (--params <json> | --file <path>)`
- `batch --file <path>`
- `verify [path|--all]`
- `editor state|play|stop|pause`
- `menu "<Unity/Menu/Path>" [--validate-only]`
- `refresh [--mode assets|scripts|all]`
- `console read|clear`
- `scene hierarchy|active|find`
- `object create|set-active|set-parent|component-get|component-set`
- `prefab instantiate|save|connect`
- `reserialize <path>...`
- `runtime state|inspect|mutate`
- `exec csharp`

## Verify Loop

The default edit loop is:

1. Modify, create, or delete a Unity `.cs` file.
2. Run `cubic-cli verify <path>` for a known changed file, or `cubic-cli verify --all` after create/delete operations.
3. Wait for compilation to settle.
4. Read compiler errors.
5. Continue scene, prefab, or runtime work only if verify succeeds.

`exec csharp` stays available, but the structured commands above are the preferred surface.

## Discovery And Safety

- `cubic-cli status --wait-ready` waits for Unity to settle after compilation or refresh work.
- `cubic-cli list` and `cubic-cli help <group.action>` expose the live Unity command catalog.
- `cubic-cli call` allows raw command passthrough for newly added Unity commands without changing the Python parser.
- `cubic-cli preflight` runs command-specific safety checks for risky operations.
- `cubic-cli batch --file ...` executes multiple commands sequentially from one JSON payload.

## Skill Install Targets

- Codex skills install to `%USERPROFILE%\.codex\skills`
- Claude Code skills install to `<UnityProject>/.claude/skills`

Both targets receive the same verify workflow, with only the wrapper wording adjusted per agent.
