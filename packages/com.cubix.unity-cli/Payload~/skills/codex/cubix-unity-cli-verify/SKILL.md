---
name: cubix-unity-cli-verify
description: Run the Cubix Unity CLI verify loop from Codex after modifying, creating, or deleting Unity C# files. Use when Codex must reimport or refresh scripts, wait for Unity compilation, read compiler errors, and block further Unity work until verify succeeds.
---

Run `cubix-cli verify <path>` immediately after editing an existing `.cs` file.

Run `cubix-cli verify --all` after creating or deleting `.cs` files, or when the exact changed path is unclear.

Stop `scene`, `object`, `prefab`, `runtime`, and `exec csharp` work while verify is failing.

Read [references/unity-cli-workflow.md](references/unity-cli-workflow.md) before improvising.
