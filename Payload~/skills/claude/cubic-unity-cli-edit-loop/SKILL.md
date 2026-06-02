---
name: cubic-unity-cli-edit-loop
description: Drive Cubic Unity edit loops from Claude Code with the installed cubic-cli command only when the current workspace is a Unity project and changed files are under `Assets/`. Use when Claude Code needs to modify Unity scripts, recompile after `Assets/**/*.cs` changes, inspect scene or runtime state, and perform prefab or object work only after the verify workflow passes. Do not use in non-Unity workspaces or for changes outside `Assets/`.
---

Use [references/unity-cli-workflow.md](references/unity-cli-workflow.md) as the default operating procedure.

Keep the loop strict:

1. Confirm the workspace is a Unity project by checking for folders such as `Assets/` and `ProjectSettings/`.
2. Use this workflow only when changed files are under `Assets/`.
3. Run `cubic-cli verify` only after creating, editing, or deleting `Assets/**/*.cs`.
4. Fix compiler errors if verify fails.
5. Resume scene, object, prefab, or runtime work only after verify passes.
6. Keep `exec csharp` as the last-resort escape hatch.
