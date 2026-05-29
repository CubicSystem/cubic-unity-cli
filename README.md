# Cubix Unity CLI

Monorepo for the `com.cubicengine.unity-cli` Unity package and its packaged payloads.
The Unity package owns the local HTTP connector, the setup window, the Python CLI
payload, and the installable Codex and Claude Code skills.

## Layout

- `packages/com.cubix.unity-cli`
  UPM package. This is the product you import into Unity.
- `packages/com.cubix.unity-cli/Editor`
  Connection service, command handlers, setup UI, and installer services.
- `packages/com.cubix.unity-cli/Payload~/python`
  Python package payload staged and installed as `cubix-cli` via `pipx`.
- `packages/com.cubix.unity-cli/Payload~/skills`
  Self-contained skill payloads for Codex and Claude Code.

## Install Flow

1. Add `com.cubicengine.unity-cli` to the Unity project's `Packages/manifest.json`.
2. Open the Unity project.
3. Open `Tools/Cubix/Unity CLI`.
4. Confirm the connection status or use `Connect` / `Reconnect`.
5. Use `Install CLI` to bootstrap `pip`, `pipx`, and the `cubix-cli` command if Python 3.10+ is available.
6. Use `Install Codex Skills`, `Install Claude Code Skills`, or `Install All Skills`.

The package auto-connects on editor load by default. You can disable that from the setup window.

## Runtime Surface

The editor connector serves loopback HTTP on `127.0.0.1` and keeps discovery files under `%USERPROFILE%\.cubix-cli`.

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
2. Run `cubix-cli verify <path>` for a known changed file, or `cubix-cli verify --all` after create/delete operations.
3. Wait for compilation to settle.
4. Read compiler errors.
5. Continue scene, prefab, or runtime work only if verify succeeds.

`exec csharp` stays available, but the structured commands above are the preferred surface.

## Discovery And Safety

- `cubix-cli status --wait-ready` waits for Unity to settle after compilation or refresh work.
- `cubix-cli list` and `cubix-cli help <group.action>` expose the live Unity command catalog.
- `cubix-cli call` allows raw command passthrough for newly added Unity commands without changing the Python parser.
- `cubix-cli preflight` runs command-specific safety checks for risky operations.
- `cubix-cli batch --file ...` executes multiple commands sequentially from one JSON payload.

## Skill Install Targets

- Codex skills install to `%USERPROFILE%\.codex\skills`
- Claude Code skills install to `<UnityProject>/.claude/skills`

Both targets receive the same verify workflow, with only the wrapper wording adjusted per agent.
