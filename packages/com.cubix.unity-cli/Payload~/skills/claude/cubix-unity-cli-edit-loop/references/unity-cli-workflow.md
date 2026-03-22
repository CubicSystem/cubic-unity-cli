# Cubix Unity CLI Workflow

Use this workflow only when all of the following are true:

- The current workspace is a Unity project.
- Folders such as `Assets/` and `ProjectSettings/` exist.
- The changed file is under `Assets/`.
- The change creates, edits, or deletes a `.cs` file.

If any condition above is false, skip this workflow.

Follow this loop whenever `Assets/**/*.cs` changes:

1. Edit, create, or delete `Assets/**/*.cs` files.
2. Run `cubix-cli verify <path>` for a known changed script under `Assets/`.
3. Run `cubix-cli verify --all` after script creation or deletion under `Assets/`, or when the changed path inside `Assets/` is unclear.
4. Wait for the verify result.
5. Read compiler errors from the verify output. If needed, run `cubix-cli console read --source compiler --level error`.
6. Stop scene, prefab, object, and runtime work until verify succeeds.

Prefer structured commands over `cubix-cli exec csharp`:

- `cubix-cli status --wait-ready`
- `cubix-cli list`
- `cubix-cli help <group.action>`
- `cubix-cli preflight <group.action>`
- `cubix-cli editor state|play|stop|pause`
- `cubix-cli menu "<Unity/Menu/Path>" --validate-only`
- `cubix-cli refresh --mode scripts`
- `cubix-cli scene active|hierarchy|find`
- `cubix-cli object create|set-active|set-parent|component-get|component-set`
- `cubix-cli prefab instantiate|save|connect`
- `cubix-cli reserialize Assets/...`
- `cubix-cli playtest --duration-seconds 5`
- `cubix-cli runtime state|inspect|mutate`
