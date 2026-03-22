---
name: cubix-unity-cli-edit-loop
description: Drive Cubix Unity editing work from Codex with the installed cubix-cli command. Use when Codex needs to iterate on Unity scripts, compile after each change, inspect scenes or runtime state, and perform prefab or object operations only after the verify workflow passes.
---

Start from [references/unity-cli-workflow.md](references/unity-cli-workflow.md).

Follow this order:

1. Edit code or assets.
2. Run `cubix-cli verify`.
3. Continue with `scene`, `object`, `prefab`, or `runtime` commands only when verify succeeds.
4. Prefer structured commands over `cubix-cli exec csharp`.
5. Use `exec csharp` only as the last resort for one-off editor automation.
