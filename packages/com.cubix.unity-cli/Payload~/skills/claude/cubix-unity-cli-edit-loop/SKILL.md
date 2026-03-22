---
name: cubix-unity-cli-edit-loop
description: Drive Cubix Unity edit loops from Claude Code with the installed cubix-cli command. Use when Claude Code needs to modify scripts, recompile after each code change, inspect scene or runtime state, and perform prefab or object work only after the verify workflow passes.
---

Use [references/unity-cli-workflow.md](references/unity-cli-workflow.md) as the default operating procedure.

Keep the loop strict:

1. Change code.
2. Run `cubix-cli verify`.
3. Fix compiler errors if verify fails.
4. Resume scene, object, prefab, or runtime work only after verify passes.
5. Keep `exec csharp` as the last-resort escape hatch.
