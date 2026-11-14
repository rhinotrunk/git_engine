# RhinoTrunk commands for Rhino 8

Prototype client that drives the RhinoTrunk engine from Rhino. The production version is C#; see [docs/CSHARP_CONVERSION_GUIDE.md](../docs/CSHARP_CONVERSION_GUIDE.md). Its purpose is to test the backend. The UI panel is out of scope: every command runs from the command line (aliases) or from an agent (script globals).

| File | Role |
| --- | --- |
| `rhinotrunk.py` | All commands, document mapping, change tracking and the display conduit |
| `rhinotrunk_gl.py` | GLSL outline renderer (raw OpenGL through ctypes, used inside the conduit) |
| `TrunkCommit.py`, `TrunkPull.py`, `TrunkBranch.py`, `TrunkMerge.py`, `TrunkCompare.py`, `TrunkStatus.py` | One-line entry points for aliases |

## Set up

Start the server (`dotnet run --project src/RhinoTrunk.Server`). Then add aliases in Rhino (Options → Aliases):

```text
TrunkCommit    ! _-RunPythonScript "C:\Users\rober\repos\git_engine\rhino\TrunkCommit.py"
TrunkPull      ! _-RunPythonScript "C:\Users\rober\repos\git_engine\rhino\TrunkPull.py"
TrunkBranch    ! _-RunPythonScript "C:\Users\rober\repos\git_engine\rhino\TrunkBranch.py"
TrunkMerge     ! _-RunPythonScript "C:\Users\rober\repos\git_engine\rhino\TrunkMerge.py"
TrunkCompare   ! _-RunPythonScript "C:\Users\rober\repos\git_engine\rhino\TrunkCompare.py"
TrunkStatus    ! _-RunPythonScript "C:\Users\rober\repos\git_engine\rhino\TrunkStatus.py"
```

Server URL: `RT_SERVER` environment variable, default `http://localhost:5280`.

The stubs start with `#! python3`: without it, Rhino 8's `RunPythonScript` runs them with the legacy IronPython 2 engine, which fails.

## Agents

Set globals and exec the module. Agents never get a prompt; the outcome is in `RT_RESULT`.

```python
RT_HOME = r"C:\Users\rober\repos\git_engine\rhino"
RT_AUTHOR = {"id": "agent:massing-bot", "name": "Massing bot", "kind": "agent"}
RT_CMD, RT_ARGS = "compare", {"options": ["agent/massing-a", "agent/massing-b", "agent/massing-c"], "mode": "overlay"}
exec(open(RT_HOME + r"\rhinotrunk.py", encoding="utf-8").read(), globals())
print(RT_RESULT)
```

Without `RT_AUTHOR`, the author is the OS user with `kind: human`.

## Commands

### COMMIT

- **Needs a saved file.**
  - Humans with unsaved edits choose `Save`, `Revert` (reopen the last save) or `Cancel`. A never-saved file opens Save As.
  - Agents save automatically; for a never-saved file they pass `path`.
- Commits the local changes against the file's commit:
  1. Uploads only the missing blobs.
  2. Publishes `preview-mesh.v1` for new geometry (other people's COMPARE needs it).
  3. Saves again, so the file and its recorded commit match.
- Agents commit freely to their own branches. The server rejects agent commits to protected branches (`main`).
- Args: `message`, `path`, `repo` + `branch` (first commit of an unlinked file).

### PULL

Brings the file to any branch, tag or commit. **Only the differences are loaded:**

1. One `diff?manifests=true` call from the file's commit to the target.
2. Local edits found by the change tracker are added to that list.
3. One batch download of the attributes for those entities. Geometry is downloaded only for objects whose geometry differs.
4. Applied in dependency order:
   1. Settings: units are adopted without scaling.
   2. Objects to delete.
   3. Layers to delete, children first.
   4. Layers to write, parents first.
   5. Objects to write.

Everything in the file that differs from the target is replaced or removed. Rhino keeps the entity ids: an object or layer deleted earlier in the session is undeleted rather than re-created, otherwise Rhino would issue a new id. A pull is one undo step, and it rolls itself back if anything fails.

- Humans confirm before uncommitted changes are discarded.
- Agents pass `force=True`.
- Args: `rev`, `force`, `repo`.

### BRANCH

- Creates a branch from the file's current commit and switches the file to it.
- Uncommitted changes stay in the file. Pass `commit=True` (agents), or answer Yes (humans), to commit them to the new branch right away.
- The server records the parent branch (the file's branch) and the creator, so branches of branches work.
- In an unlinked file, BRANCH makes the first commit on the new branch.
- Args: `name`, `commit`, `message`.

### MERGE

Merges the file's branch into its parent (recorded at BRANCH; default `main`). `into` and `theirs` override this.

1. **Preview.** The merged result is drawn in the conduit, highlighted relative to the target branch. The HUD shows incoming counts, conflicts, rule resolutions and approval reasons.
2. **Conflicts:**
   - Humans get a diffing session. For each conflicting entity, the viewport zooms to it, `Toggle` flips between our and their version (solid, blue outline; the other side ghosted, amber outline), and the human picks `Ours`/`Theirs` (or `AllOurs`/`AllTheirs`). The preview then reruns with those resolutions.
   - Agents pass `resolutions=[{"kind", "id", "take"}]`.
3. **Rule resolutions** (repository `mergeRules`) are listed in the HUD and in the confirmation prompt. Confirming sends `approveRuleResolutions`.
4. **Approval:**
   - A human's `Merge` confirmation is the approval.
   - An agent needs one for protected targets, foreign branches and rule resolutions. Without an active approval it posts a `review/merge-request` annotation, leaves the preview on screen and stops (`RT_RESULT.status = "approval_required"`). With one (a human pressed Approve in COMPARE), it merges.
5. Optional `switch`: pull the target afterwards.

### COMPARE

Reviews merge proposals (every unmerged, unprotected branch by default, or `options`) against a point on main (`at`, default: the file's commit). Each option is a server merge preview, so conflicts and approval needs are shown too.

| Mode | What you see |
| --- | --- |
| `cycle` | One option at a time; `A`–`Z` / `Next` switch instantly (geometry cached by content hash) |
| `overlay` | All options at once; the active one solid, the others ghosted with thin outlines |
| `side` | The file plus one copy of the model per option, side by side in the same viewport, drawn by the conduit (no extra viewports) |

Relative to the current file:

- **green outline**: new geometry.
- **red outline**: removed geometry, drawn ghosted.
- **blue outline**: modified geometry. The current version is hidden and the new one shown.
- **amber**: a conflict.

Outlines are drawn *beneath* the object by a GLSL shader (see below). Curves and points get a thick halo in the same color.

Interactive keys: `A..Z`, `Next`, `Cycle`, `Overlay`, `SideBySide`, `Approve` (human: approve the active option into the target), `Merge`, `Bench`, Esc (exit; the file is untouched).

Args: `options`, `at`, `branch`, `mode`, `show`, `approve`, `frame`, `bench`.

### STATUS / OFF

`status` reports local changes against the file's commit. `off` hides any preview.

## Change tracking

- Document events mark candidates:
  - add, delete, undelete and replace mark the geometry;
  - attribute edits mark only the attributes, so untouched geometry is never re-serialized (which could produce different bytes).
- STATUS compares candidates with the base map (`rhinotrunk.basemap`: entity → version, geometry hash, attribute fingerprint, layer).
- Layers and settings are always compared, since those tables are small.
- The candidate list is saved in the file on save.
- If the file was modified before the tracker started this session, STATUS falls back to a full scan.

## GLSL outlines

RhinoCommon cannot attach custom shaders, so `rhinotrunk_gl.py` calls OpenGL directly (ctypes, `wglGetProcAddress`) while Rhino's context is current inside the conduit. It saves and restores all GL state it touches.

For each mesh, before Rhino draws it:

1. Mark the mesh's pixels in the stencil buffer.
2. Draw the mesh again with a vertex shader that pushes every vertex out along its welded normal by N screen pixels, only where the stencil is not set. The result is a band around the silhouette, depth-tested against the scene.
3. Clear the stencil.

The shaded or ghosted mesh is then drawn on top, so the outline sits beneath the object and still works under transparent ghosts. If GL setup fails, the conduit falls back to colored wires (`RT_RESULT.glFallback` says why).

## Measured (pavilion-options, Rhino 8, local server)

| | |
| --- | --- |
| PULL into an empty file | 330 ms (4 HTTP calls) |
| PULL between options | 130–460 ms; attribute-only changes 16–24 ms |
| COMMIT | 40–60 ms + preview publishing |
| COMPARE switch | cycle 9–35 ms, overlay 30–75 ms, side by side 33–65 ms |

## Not tested

The interactive prompts (GetOption loops, Save/Revert, the conflict session) can only be driven by a person. The agent paths, which share all the logic, were run end to end.
