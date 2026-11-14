# Converting the Python prototype to C# Rhino commands

For the team building the production Rhino 8 plugin (`rhino_client`). The Python commands in `rhino/` are a working prototype of COMMIT, PULL, BRANCH, MERGE and COMPARE against the RhinoTrunk engine. This guide explains:

- what to carry over;
- what to change;
- what we learned the hard way.

Read with:

- [CLIENT_GUIDE.md](CLIENT_GUIDE.md): data mapping;
- [API.md](API.md): endpoints;
- [BACKEND_CHANGES.md](BACKEND_CHANGES.md): the server features these commands rely on.

The prototype is the reference for **behaviour**, not for structure. It is one exec'd file because Rhino's script runner made that easiest; the C# version should be a normal plugin with services and commands.

## 1. What to reuse as is

| Piece | Where | Notes |
| --- | --- | --- |
| HTTP client and DTOs | `src/RhinoTrunk.Client`, `src/RhinoTrunk.Contracts` | Already have every call the commands use: `CreateBranchAsync`, `PreviewMergeAsync`, `CreateApprovalAsync`, `ListApprovalsAsync`, `GetPolicyAsync`, `MergeBaseAsync`, `DiffAsync(..., includeManifests: true)`, plus the existing commit, merge, batch and derived calls. Reference the projects as described in CLIENT_GUIDE §1. |
| Error codes | `ErrorCodes` in Contracts | Branch on `RhinoTrunkApiException.Error.Code`: `ref_conflict`, `merge_conflict`, `forbidden`, `approval_required` (with `Data["reasons"]`), `approval_stale`, `validation_failed`. |
| Attribute JSON schema | CLIENT_GUIDE §3; `object_attrs`, `layer_attrs`, `settings_attrs` in the prototype | Keep the key names identical so Python agents, the sample generator and the plugin produce the same attribute blobs. |
| Geometry encoding | `ToJSON` with `SerializationOptions { RhinoVersion = 8, WriteRenderMeshes = false, WriteAnalysisMeshes = false, WriteUserData = false }` | Same as CLIENT_GUIDE §4. |
| Preview recipe | `preview-mesh.v1` | Coarse joined mesh, `ToJSON`, uploaded and registered with `SetDerivedAsync` after every commit. COMPARE and MERGE depend on it. |

## 2. Suggested C# structure

| Prototype (`rhinotrunk.py`) | C# | Notes |
| --- | --- | --- |
| `cmd_commit`, `cmd_pull`, `cmd_branch`, `cmd_merge`, `cmd_compare`, `cmd_status` | One `Rhino.Commands.Command` each: `TrunkCommit`, `TrunkPull`, `TrunkBranch`, `TrunkMerge`, `TrunkCompare`, `TrunkStatus` | Thin. Parse options, call a service, report. |
| `State` (doc strings) | `WorkingCopyState` service per `RhinoDoc` | Same keys; see §3. |
| `Tracker` | `ChangeTracker`: plugin-lifetime singleton, subscribes in `OnLoad` | See §4. |
| `compute_status` | `StatusService.ComputeAsync(doc)` | Returns changes, blobs and new base entries. |
| `plan_pull` + `apply_plan` | `PullService` | Network on a background task, document edits on the UI thread; see §5. |
| `preview_merge`, `conflict_session` | `MergeService` + a `ConflictReview` UI | |
| `cmd_compare`, `build_diff`, `Scene`, `Drawable` | `CompareService` + `PreviewScene` model | |
| `make_conduit` | `TrunkConduit : DisplayConduit`, one instance per plugin | See §7. |
| `rhinotrunk_gl.OutlineRenderer` | `OutlineRenderer` | See §8. |
| `choose`, `ask_string`, `is_agent`, `RT_ARGS`, `RT_RESULT` | Command options + an `IInteraction` abstraction | See §6. |
| `S["geoms"]`, `S["attrs"]`, `S["meshes"]` (sticky caches) | `ContentCache`, keyed by content hash | Immutable by construction; can also live on disk ("cached by hash forever", CLIENT_GUIDE §7). |

UI panel: the commands are the API. The panel should call the same services, and never reimplement a flow.

## 3. Working-copy state

These are stored in `RhinoDoc.Strings`, so they are saved with the `.3dm` and a revert-to-saved stays consistent:

| Key | Value |
| --- | --- |
| `rhinotrunk.server`, `rhinotrunk.repo`, `rhinotrunk.branch`, `rhinotrunk.base` | Where the file is. An empty `branch` after pulling a tag or commit means detached. |
| `rhinotrunk.basemap` | JSON `{"kind/id": [version, geometryHash, attributesFingerprint, layerId?]}` |
| `rhinotrunk.dirty` | JSON list of change-tracker candidates, written on `BeginSaveDocument` |

- **Base map entries:**
  - `geometryHash` is the hash of the stored geometry blob (the server's hash after a pull, ours after a commit).
  - `attributesFingerprint` is a client-side hash of our attribute dictionary, re-read from Rhino after writing it. Compare only fingerprints we computed ourselves; the server's canonical JSON hash differs.
  - `layerId` (objects only) detects layer moves.
- **Exclusions:** the `rhinotrunk.*` keys must be excluded when you serialize document user text into the `settings` entity.
- **Size:** in production, consider user data on the document, or a sidecar (CLIENT_GUIDE §5), once base maps reach 100k entries. The doc string was fine for the prototype.

## 4. Change detection (STATUS, COMMIT, PULL)

**Rule: never re-serialize geometry you did not touch.** We hit this in testing: renaming an object re-serialized its brep, and after the object had been displayed and meshed, `ToJSON` produced different bytes. The rename was committed as a geometry change.

- **Geometry candidates:** `AddRhinoObject`, `DeleteRhinoObject`, `UndeleteRhinoObject`, `ReplaceRhinoObject`. Move, transform and edit all arrive as Replace.
- **Attribute-only candidates:** `ModifyObjectAttributes`. Status reuses the base map's geometry hash for these and serializes only the attributes.
- **Layers and settings:** compare them all on every status (small tables). Their table events are optional.
- **Suspend tracking** while your own PULL or COMMIT writes to the document, then clear candidates for what you wrote.
- **Persistence:** write the candidate list to `rhinotrunk.dirty` on `BeginSaveDocument` and restore it on `EndOpenDocument`.
  - If the tracker was not running when the file was opened, trust the persisted list only when `doc.Modified` is false.
  - Otherwise do a full scan, which serializes everything and can report false geometry changes. Log it.
- Undo and redo raise the same events. Candidates are only candidates: status compares against the base map, so an undone edit reports no change.

## 5. Command behaviour to preserve

### COMMIT

1. **Saved file required.**
   - Never saved: Save As.
   - Unsaved edits: offer `Save`, `Revert` (reopen from disk) or `Cancel`.
   - Agents save without asking.
   - Use `RhinoDoc.Save()`. Do **not** use `RhinoDoc.SaveAs(path)` for a never-saved file: it writes a copy and leaves `doc.Path` empty. Use the `_-SaveAs "path"` command or the Rhino 8 equivalent that re-points the document.
2. **Branch checks.** Refuse early (and the server enforces it anyway):
   - an agent committing to a protected branch (`GetBranchInfoAsync(...).Protected`);
   - a branch head that is not the file's `base` (tell the user to pull or branch).
3. **Status, upload, commit.**
   1. Compute status, then upload only missing blobs.
   2. `CommitAsync` with `ExpectedHead = base`. Use `ExpectedHead = null` plus `Parent` only for the first commit of a new branch.
   3. Store `response.Versions` into the base map, set `base`, clear the tracker.
4. **Publish previews.** Publish `preview-mesh.v1` for new geometry hashes, off the UI thread and non-blocking. Then save again, so the file on disk records the new base.

### PULL (speed matters)

1. **Linked file:** one `DiffAsync(repo, base, target, includeManifests: true)`, plus the local candidates from §4. Unlinked file or another repository: diff from empty, and treat everything in the file as local, so it is removed unless the target has it.
2. **Entity versions.** Local edits not in the server diff revert to the base map's version (unchanged between base and target). Locally added entities are deleted.
3. **One batch download:**
   - attribute blobs for every entity to write;
   - geometry **only** for added objects, objects whose `components` include `geometry` (or `subtypeChanged`), and objects with local geometry edits.

   Attribute-only changes download no geometry.
4. **Apply on the UI thread, in this order:**
   1. Settings: adopt units without scaling.
   2. Objects to delete.
   3. Layers to delete, children first. Retry any failures at the end.
   4. Layers to write, parents first.
   5. Objects to write.
   6. The retried layer deletions.
5. **Undo:** wrap the whole apply in `BeginUndoRecord`/`EndUndoRecord`. One Ctrl+Z reverts a pull, and on any exception `Undo()` rolls back. The base map is saved only after success.
6. **Keep entity ids:**
   - Set `ObjectAttributes.ObjectId` and `Layer.Id` before adding.
   - **Deleted items still own their ids.** If an object or layer with that id was deleted earlier in the session, Rhino silently gives the re-added item a **new** id.
     - Objects: undelete with `ObjectTable.Undelete`, found by enumerating with `ObjectEnumeratorSettings.DeletedObjects = true`.
     - Layers: `LayerTable.FindId` skips deleted layers, so iterate the table, then call `Undelete(index)` and modify.
   - Afterwards, verify the id and fail loudly if it is wrong.
7. **Replace geometry** with `Objects.Replace(id, geometry, ignoreModes: true)` and attributes with `ModifyAttributes(id, attrs, true)`. Use `Delete(obj, quiet: true, ignoreModes: true)` so locked or hidden objects do not block the pull.
8. **Confirm** before discarding uncommitted changes (humans). Agents must pass an explicit force flag.

| Measured in the prototype | |
| --- | --- |
| Pull into an empty file | 330 ms (4 HTTP calls) |
| Pull between options | 130–460 ms |
| Attribute-only pull | 16–24 ms |
| Re-pull that only removes local strays | 34 ms |

C# should beat these: most of the prototype's time is Python marshalling and blob download.

### BRANCH

- `CreateBranchAsync(new CreateBranchRequest { Name, From = base, Parent = currentBranch, Author })`, then switch `rhinotrunk.branch`.
- Uncommitted changes stay in the file. Offer to commit them to the new branch.
- An unlinked file has no commit to branch from, so its first commit creates the branch.

### MERGE

1. **Target:** `BranchInfo.Parent` of the file's branch, default `main`.
2. **Commit first:** require committed local changes (offer to commit).
3. **Preview twice:**
   - `PreviewMergeAsync { Branch = target, Theirs, Author }` gives what lands on the target. These changes are the highlights.
   - The same call with `CompareTo = base` gives everything else needed to make the view equal the merged result: draw those changes without outlines and hide the document versions.

   With the file on the branch being merged, a preview relative to the file is empty, which is why both are needed. A combined server call is listed as future work.
4. **Conflicts:**
   - Humans get a review UI. For each conflict, zoom to it, toggle between `Ours` and `Theirs` (solid with a blue outline; the other side ghosted with an amber outline), pick one, or pick all for the remainder. Re-preview with the `Resolutions`.
   - Agents supply `Resolutions` themselves.
5. **Rule resolutions** (`AutoResolved`): show them in the HUD and in the confirmation text, and send `ApproveRuleResolutions = true` on the human's confirmation.
6. **Approval:**
   - **Human:** the confirmation is the approval.
   - **Agent with `RequiresApproval`:**
     1. Look for an active approval (`ListApprovalsAsync(branch: target, theirs: branch)`) whose `Theirs` equals the previewed commit.
     2. If found, merge with `Approval = id`.
     3. If not, post a `review/merge-request` annotation on the head, leave the preview up, and stop with a clear result.
7. Merge with `ExpectedHead = preview.Ours` and `Theirs = preview.Theirs` (commit hashes, not names), so the user merges exactly what they saw.
8. Optionally pull the target afterwards.

### COMPARE

- **Options:**
  - Default: every unprotected branch except the target and the file's branch, children of the target first.
  - Drop options whose preview is `upToDate`.
  - At most 26, lettered A–Z.
- **One preview per option:** `PreviewMergeAsync { Ours = at, Branch = target, Theirs = option, CompareTo = base, Manifests = true }`. `at` defaults to the file's commit ("a given point on main").
- **Load drawables once per option:**
  - preview meshes via `GetDerivedAsync`, falling back to the real geometry, meshed locally;
  - curves and points from real geometry.

  Switching must not touch the network.
- **Modes:**

| Mode | Behaviour |
| --- | --- |
| Cycle | One option. |
| Overlay | All options; the active one opaque with 5 px outlines, the others at transparency 0.75 with 2 px outlines. The document objects hidden are those of the active option only. |
| Side by side | Slot 0 is the real document. Each option slot `i` is offset by `(width × 1.3 + 5) × i` along X. Unchanged document objects are drawn with `DisplayPipeline.DrawObject(obj, xform)`; option items use `PushModelTransform`. Frame the row from the front. Labels: a big letter above the slot and the branch name at its front edge. |

- **Actions:** `Approve` creates an approval (humans only; the server rejects agents). `Merge` hands over to MERGE with `into = target` and `theirs = option`.

## 6. Prompts, agents and command options

The prototype has two modes:

- **Human:** `GetOption` and `GetString` prompts.
- **Agent:** `RT_AUTHOR.kind == "agent"`; never prompt, take everything from `RT_ARGS`, return `RT_RESULT`.

In C#:

- Give every command a scriptable dash form (`-TrunkPull Rev=main Force=Yes`) so agents and macros can run it through `RhinoApp.RunScript`. Use `RunMode.Scripted` to skip prompts.
- Put the decisions behind one interface (`IInteraction.Choose`, `AskString`, `Confirm`) with a human and an agent implementation. The flows then contain no `if agent` branches beyond policy.
- Agent identity comes from the agent host (MCP session, API key) in production, not from a script global. Until the server has auth, it is self-declared (OPEN_QUESTIONS #4).
- Return a structured result (status, commit, counts, `approval_required` reasons, conflicts) to the agent host. The prototype's `RT_RESULT` keys are a good starting schema.

## 7. Display conduit

- **One conduit, one scene.** Commands build an immutable `PreviewScene` and swap it in:
  - drawables: mesh, curve or point; fill colour; transparency; outline colour or none; outline width; offset;
  - hidden document ids;
  - document copies with transforms;
  - labels and HUD lines;
  - bounding box.

  Swapping is then a pointer change plus `Redraw`. Measured switch times in the prototype: cycle 9–35 ms, overlay 30–75 ms, side by side 33–65 ms.
- **Overrides:**

| Override | Use |
| --- | --- |
| `CalculateBoundingBox` | Clipping. |
| `CalculateBoundingBoxZoomExtents` | Without it, Zoom Extents ignores the preview. |
| `ObjectCulling` | Hide document objects that are modified or removed in the preview. |
| `PostDrawObjects` | Outlines first, then opaque, then transparent items. |
| `DrawForeground` | HUD. |

- **Colours** (semantic, the same in every mode):

| Colour | RGB | Meaning |
| --- | --- | --- |
| Green | `0,220,70` | New to the current file |
| Red | `235,40,40` | Removed; drawn ghosted at transparency 0.8 |
| Blue | `40,120,255` | Modified; the current version is hidden |
| Amber | `255,170,0` | Conflict |

- **Curves and points:** draw a thicker curve or point in the outline colour first, then the item itself.
- **False-colour meshes** (vertex colours count equal to vertex count): use `DrawMeshFalseColors` when opaque.
- **Cache keys:** content hashes for server geometry; `doc:<id>:<RuntimeSerialNumber>` for document objects.

## 8. GLSL outlines (drawn beneath the object)

RhinoCommon has no API for custom shaders, in Python or C#. The prototype calls OpenGL directly while Rhino's context is current inside `PostDrawObjects`, on Windows. Port it with P/Invoke on `opengl32.dll` plus `wglGetProcAddress`, or with OpenTK bindings loaded against the current context.

**Algorithm**, per mesh, before Rhino draws the mesh:

1. **Mask.** Colour and depth writes off, depth function `ALWAYS`, stencil `ALWAYS` with ref 1 and `REPLACE`. Draw the mesh.
2. **Hull.** Colour on, depth writes off, depth function `LEQUAL`, stencil `NOTEQUAL 1`. Draw the mesh with the vertex shader pushing each vertex outward along its normal by N screen pixels:

   ```glsl
   vec4 c = uMVP * vec4(aPos, 1.0);
   vec4 n = uMVP * vec4(aNrm, 0.0);
   vec2 d = (n.xy * c.w - c.xy * n.w) * uViewport;   // screen-space normal direction
   c.xy += normalize(d) * (2.0 * uWidth / uViewport) * c.w;
   ```

3. **Clear.** Stencil `ALWAYS` with ref 0 and `REPLACE`, colour off. Draw the mesh again.

**Inputs:**

- `uMVP` = `viewport.GetTransform(CoordinateSystem.World, CoordinateSystem.Clip)`, multiplied by the item's offset translation. It is row-major, so upload it with `transpose = GL_TRUE`. Rhino's clip space matched GL's depth convention in our tests.
- `uViewport` comes from `glGetIntegerv(GL_VIEWPORT)`, which works for screen and `CaptureToBitmap` alike.
- **Welded normals:** average vertex normals over coincident positions. Otherwise hard edges (boxes) crack the hull.

**Rules:**

- Save and restore **every** state you touch: program, VAO, array buffer, depth, stencil and cull enables, depth mask, colour mask, depth function, stencil function, operations and masks. Rhino's own drawing breaks otherwise.
- Create VAOs and VBOs per GL context (key them by `wglGetCurrentContext()`). Build the CPU arrays once per mesh hash.
- On any GL failure, disable the renderer for the session and fall back to `DrawMeshWires` in the outline colour. Never let an exception escape a conduit callback.
- **Mac:** Rhino 8 on macOS draws with Metal, so this path does not apply. Plan the silhouette fallback (`Mesh.GetOutlines(viewport)` → thick curves drawn before the mesh, depth-tested) as the cross-platform baseline, with GL as the Windows enhancement.

Why the stencil instead of a plain inverted hull: the band appears only *outside* the silhouette, so a ghosted (transparent) removed object shows a clean red rim instead of a red fill through it.

## 9. Threading and performance

- **Network on background tasks, document edits on the UI thread** (`RhinoApp.InvokeOnUiThread`). The prototype does everything on the UI thread; do not copy that.
- **Prefetch.** COMPARE previews per option are independent: run them in parallel. MERGE's two previews are independent too.
- **Batching.** Batch `GetDerivedAsync` calls: the prototype calls once per geometry hash. A bulk derived lookup on the server is a cheap addition if this shows up in profiles.
- **Redraw.** Set `RedrawEnabled = false` during PULL apply and redraw once at the end.
- **Caches.** Keep `ContentCache` across commands and documents. Hashes never change meaning.

## 10. Test checklist (what the prototype was verified against)

Use repository `pavilion-options` (generated by `samples/rhino_design_options.py`).

1. PULL `main` into a new file: the file's default layers are removed, 27 objects on 6 layers, units Meters, status reports 0 changes.
2. PULL across `option/a…c`, `agent/subd-canopy`, tag `v0-site`, `main`, option A, `main`:
   - every step writes only the differences;
   - ids are preserved across delete and re-add;
   - status reports 0 changes at the end.
3. Rename an object: status shows one modification and no geometry blob. PULL reverts it without downloading geometry.
4. BRANCH as an agent, move a tree, delete a grid line, add a box, COMMIT. The commit carries 3 changes and publishes previews.
5. MERGE as an agent into `main`:
   - `approval_required`;
   - a `review/merge-request` annotation is posted;
   - the preview shows a green box, a blue tree and a red ghost of the grid line, relative to `main`.
6. A human approves in COMPARE; the agent's MERGE then succeeds (fast-forward) and the approval shows `used`. *Covered by server tests; not yet run end to end in Rhino, because it needs a person to approve.*
7. Conflict: two agent branches rename the same object differently. MERGE reports one conflict on `components.attributes`; with a `theirs` resolution, the agent merges into its own branch with no approval.
8. COMPARE with 4–6 options in all three modes: switch times as listed in §7, and green outlines visible beneath new geometry.
9. A failed PULL (simulate by killing the server mid-apply) leaves the file unchanged, thanks to the undo rollback.

Not yet covered by the prototype: block definitions and instances, materials, linetypes, groups, hatches, dimension styles, notes in the document, and page-space objects. Their mapping is in CLIENT_GUIDE §2.
