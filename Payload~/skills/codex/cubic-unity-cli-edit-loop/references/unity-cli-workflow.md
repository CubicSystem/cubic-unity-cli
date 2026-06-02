# Cubic Unity CLI Workflow

Use this workflow only when all of the following are true:

- The current workspace is a Unity project.
- Folders such as `Assets/` and `ProjectSettings/` exist.
- The changed file is under `Assets/`.
- The change creates, edits, or deletes a `.cs` file.

If any condition above is false, skip this workflow.

Follow this loop whenever `Assets/**/*.cs` changes:

1. Edit, create, or delete `Assets/**/*.cs` files.
2. Run `cubic-cli verify <path>` for a known changed script under `Assets/`.
3. Run `cubic-cli verify --all` after script creation or deletion under `Assets/`, or when the changed path inside `Assets/` is unclear.
4. Wait for the verify result.
5. Read compiler errors from the verify output. If needed, run `cubic-cli console read --source compiler --level error`.
6. Stop scene, prefab, object, and runtime work until verify succeeds.

Prefer structured commands over `cubic-cli exec csharp`:

- `cubic-cli status --wait-ready`
- `cubic-cli list`
- `cubic-cli help <group.action>`
- `cubic-cli preflight <group.action>`
- `cubic-cli editor state|play|stop|pause`
- `cubic-cli menu "<Unity/Menu/Path>" --validate-only`
- `cubic-cli refresh --mode scripts`
- `cubic-cli scene active|open|hierarchy|find`
- `cubic-cli object create|set-active|set-parent|component-get|component-set`
- `cubic-cli prefab instantiate|save|connect`
- `cubic-cli reserialize Assets/...`
- `cubic-cli test run --platform EditMode`
- `cubic-cli playtest --duration-seconds 5`
- `cubic-cli runtime state|inspect|mutate`
