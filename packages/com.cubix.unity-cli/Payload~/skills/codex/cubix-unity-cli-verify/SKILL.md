---
name: cubix-unity-cli-verify
description: Run the Cubix Unity CLI verify loop from Codex only when the current workspace is a Unity project and changed files are C# scripts under `Assets/`. Use when Codex must reimport or refresh Unity scripts, wait for Unity compilation, read compiler errors, and block further Unity work until verify succeeds. Do not use in non-Unity workspaces or for changes outside `Assets/`.
---

Confirm the workspace is a Unity project before using this skill. Treat a workspace as Unity only when folders such as `Assets/` and `ProjectSettings/` exist.

Use this skill only when the modified path is under `Assets/` and the change creates, edits, or deletes a `.cs` file.

Run `cubix-cli verify <path>` immediately after editing an existing `Assets/**/*.cs` file.

Run `cubix-cli verify --all` after creating or deleting `Assets/**/*.cs` files, or when the exact changed path under `Assets/` is unclear.

Do not run this verify workflow for non-Unity projects, non-`Assets/` changes, or non-C# changes.

Stop `scene`, `object`, `prefab`, `runtime`, and `exec csharp` work while verify is failing.

Read [references/unity-cli-workflow.md](references/unity-cli-workflow.md) before improvising.
