---
name: cubic-unity-cli-edit-loop
description: Drive Cubic Unity editing work from Codex with the installed cubic-cli command only when the current workspace is a Unity project and changed files are under `Assets/`. Use when Codex needs to iterate on Unity scripts, compile after `Assets/**/*.cs` changes, inspect scenes or runtime state, and perform prefab or object operations only after the verify workflow passes. Do not use in non-Unity workspaces or for changes outside `Assets/`.
---

Start from [references/unity-cli-workflow.md](references/unity-cli-workflow.md).

Follow this order:

1. Confirm the workspace is a Unity project by checking for folders such as `Assets/` and `ProjectSettings/`.
2. Use this workflow only when changed files are under `Assets/`.
3. Run `cubic-cli verify` only after creating, editing, or deleting `Assets/**/*.cs`.
4. Continue with `scene`, `object`, `prefab`, or `runtime` commands only when verify succeeds.
5. Prefer structured commands over `cubic-cli exec csharp`.
6. Use `exec csharp` only as the last resort for one-off editor automation.
