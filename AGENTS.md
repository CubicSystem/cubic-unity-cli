# Cubic Unity CLI Agents Guide

## Scope

This repository builds the Cubic Unity CLI package and its installable payloads.

- Unity package: `packages/com.cubicengine.unity-cli`
- Setup window: `Tools/CubicEngine/UnityCli`
- CLI payload source: `packages/com.cubicengine.unity-cli/Payload~/python`
- Skill payload source: `packages/com.cubicengine.unity-cli/Payload~/skills`

Do not edit installed user skills under home directories. Edit the payload sources inside this repository instead.

## Product Rules

- The product name is `Cubic Unity CLI`.
- The UPM package id is `com.cubicengine.unity-cli`.
- The shell command is `cubic-cli`.
- The connector is an internal subsystem, not the product name.

## Versioning Rules

- Keep the UPM package version and the installable `cubic-cli` payload version aligned for each released package update.
- If Unity package C# or editor UI code changes and that change must be delivered through Package Manager, bump both `packages/com.cubicengine.unity-cli/package.json` and the CLI payload version files.
- If the installable `cubic-cli` behavior changes and users must reinstall or update the CLI payload, bump the aligned package and CLI versions together.
- Do not rely only on capability checks when a CLI reinstall is required for the fix to take effect. Use a version change so the setup window can clearly detect drift and prompt `Reinstall CLI`.
- Skill payload versions remain independent. Do not bump skill payload versions unless the skill payload content changes.

## Expected Workflow

The default Unity coding loop is:

1. Edit, create, or delete Unity `.cs` files.
2. Run `cubic-cli verify <path>` for a known edited file.
3. Run `cubic-cli verify --all` after script creation or deletion, or when the exact path is unclear.
4. Wait for compilation to finish.
5. Read compiler errors.
6. Continue only when verify succeeds.

Scene, prefab, and runtime work should happen only after the verify workflow passes.

## Command Preference

Prefer structured commands over raw execution:

- `cubic-cli verify`
- `cubic-cli status`
- `cubic-cli list`
- `cubic-cli help <group.action>`
- `cubic-cli call <group.action>`
- `cubic-cli preflight <group.action>`
- `cubic-cli batch --file ...`
- `cubic-cli editor ...`
- `cubic-cli menu ...`
- `cubic-cli refresh ...`
- `cubic-cli console ...`
- `cubic-cli scene ...`
- `cubic-cli object ...`
- `cubic-cli prefab ...`
- `cubic-cli reserialize ...`
- `cubic-cli runtime ...`

Use `cubic-cli exec csharp` only as a last resort when the structured surface cannot express the required operation.

Use `cubic-cli list` and `cubic-cli help` to discover newly added Unity commands before falling back to `exec`.

## Setup Window Responsibilities

The Unity setup window is the primary operator surface for installation and status.

- `Connection` manages connect, disconnect, reconnect, refresh, and auto-connect.
- `CLI` diagnoses Python, pip, pipx, and the installed `cubic-cli` command.
- `CLI` also reports command catalog reachability and advanced CLI capability status.
- `Skills` installs or removes Codex and Claude Code skills.

Python 3.10+ is required for CLI installation. The package may bootstrap `pip` and `pipx`, but it does not install Python itself.

## Skill Install Targets

- Codex skills install to `%USERPROFILE%\.codex\skills`
- Claude Code skills install to `<UnityProject>/.claude/skills`

Each target gets two self-contained skills:

- `cubic-unity-cli-verify`
- `cubic-unity-cli-edit-loop`

The installed skills must remain self-contained. Each skill folder should carry the workflow reference it needs.

## Connector Files

- Instance discovery files live under `%USERPROFILE%\.cubic-cli\instances`
- Status files live under `%USERPROFILE%\.cubic-cli\status`

The connector serves loopback HTTP only and should stay bound to `127.0.0.1`.
