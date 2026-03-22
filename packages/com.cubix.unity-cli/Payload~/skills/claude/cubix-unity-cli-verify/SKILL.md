---
name: cubix-unity-cli-verify
description: Run the Cubix Unity CLI verify loop from Claude Code after modifying, creating, or deleting Unity C# files. Use when Claude Code must reimport or refresh scripts, wait for Unity compilation, read compiler errors, and stop further Unity work until verify succeeds.
---

Run `cubix-cli verify <path>` right after editing an existing Unity `.cs` file.

Run `cubix-cli verify --all` after creating or deleting scripts, or when the modified path is ambiguous.

Do not continue to prefab, scene, runtime, or `exec csharp` work while verify is failing.

Read [references/unity-cli-workflow.md](references/unity-cli-workflow.md) for the full command surface.
