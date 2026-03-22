# Cubix Unity CLI Agents Guide

## Scope

This repository builds the Cubix Unity CLI package and its installable payloads.

- Unity package: `packages/com.cubix.unity-cli`
- Setup window: `Tools/Cubix/Unity CLI`
- CLI payload source: `packages/com.cubix.unity-cli/Payload~/python`
- Skill payload source: `packages/com.cubix.unity-cli/Payload~/skills`

Do not edit installed user skills under home directories. Edit the payload sources inside this repository instead.

## Product Rules

- The product name is `Cubix Unity CLI`.
- The UPM package id is `com.cubix.unity-cli`.
- The shell command is `cubix-cli`.
- The connector is an internal subsystem, not the product name.

## Expected Workflow

The default Unity coding loop is:

1. Edit, create, or delete Unity `.cs` files.
2. Run `cubix-cli verify <path>` for a known edited file.
3. Run `cubix-cli verify --all` after script creation or deletion, or when the exact path is unclear.
4. Wait for compilation to finish.
5. Read compiler errors.
6. Continue only when verify succeeds.

Scene, prefab, and runtime work should happen only after the verify workflow passes.

## Command Preference

Prefer structured commands over raw execution:

- `cubix-cli verify`
- `cubix-cli status`
- `cubix-cli list`
- `cubix-cli help <group.action>`
- `cubix-cli call <group.action>`
- `cubix-cli preflight <group.action>`
- `cubix-cli batch --file ...`
- `cubix-cli editor ...`
- `cubix-cli menu ...`
- `cubix-cli refresh ...`
- `cubix-cli console ...`
- `cubix-cli scene ...`
- `cubix-cli object ...`
- `cubix-cli prefab ...`
- `cubix-cli reserialize ...`
- `cubix-cli runtime ...`

Use `cubix-cli exec csharp` only as a last resort when the structured surface cannot express the required operation.

Use `cubix-cli list` and `cubix-cli help` to discover newly added Unity commands before falling back to `exec`.

## Setup Window Responsibilities

The Unity setup window is the primary operator surface for installation and status.

- `Connection` manages connect, disconnect, reconnect, refresh, and auto-connect.
- `CLI` diagnoses Python, pip, pipx, and the installed `cubix-cli` command.
- `CLI` also reports command catalog reachability and advanced CLI capability status.
- `Skills` installs or removes Codex and Claude Code skills.

Python 3.10+ is required for CLI installation. The package may bootstrap `pip` and `pipx`, but it does not install Python itself.

## Skill Install Targets

- Codex skills install to `%USERPROFILE%\.codex\skills`
- Claude Code skills install to `<UnityProject>/.claude/skills`

Each target gets two self-contained skills:

- `cubix-unity-cli-verify`
- `cubix-unity-cli-edit-loop`

The installed skills must remain self-contained. Each skill folder should carry the workflow reference it needs.

## Connector Files

- Instance discovery files live under `%USERPROFILE%\.cubix-cli\instances`
- Status files live under `%USERPROFILE%\.cubix-cli\status`

The connector serves loopback HTTP only and should stay bound to `127.0.0.1`.
