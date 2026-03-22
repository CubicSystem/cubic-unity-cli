# Cubix Unity CLI Workflow

Follow this loop whenever Unity C# files change:

1. Edit, create, or delete `.cs` files.
2. Run `cubix-cli verify <path>` for a known changed script.
3. Run `cubix-cli verify --all` after script creation or deletion, or when the changed path is unclear.
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
- `cubix-cli runtime state|inspect|mutate`
