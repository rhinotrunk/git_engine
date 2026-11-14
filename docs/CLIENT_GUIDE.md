# Rhino client guide (prototype v0.1)

How the Rhino 8 plugin (`rhino_client`) talks to RhinoTrunk. The server knows nothing about Rhino: the plugin turns a `RhinoDoc` into entities and back. Everything here is a proposal for the prototype; items marked **(open)** are listed in [OPEN_QUESTIONS.md](OPEN_QUESTIONS.md).

## 1. Reference the SDK

`src/RhinoTrunk.Client` (typed HTTP client) and `src/RhinoTrunk.Contracts` (DTOs, canonical JSON, hashing) target `net8.0` and have no third-party dependencies, so they load safely in Rhino 8.20+ on .NET 8. With both repositories checked out side by side, add to `rhino_client/rhino/plugin/RhinoClient.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="../../../git_engine/src/RhinoTrunk.Client/RhinoTrunk.Client.csproj" />
</ItemGroup>
```

```csharp
var api = new RhinoTrunkClient(new Uri("http://localhost:5280/"));
var head = await api.GetBranchAsync("tower", "main");          // null if the branch does not exist
```

Never block Rhino's UI thread on HTTP: run calls on a background task and marshal results back with `RhinoApp.InvokeOnUiThread`.

## 2. Map the document to entities

| Kind | RhinoCommon source | Entity id | Components | Refs (role) |
| --- | --- | --- | --- | --- |
| `settings` | `ModelUnitSystem`, `ModelAbsoluteTolerance`, `ModelAngleToleranceRadians`, `RhinoDoc.Strings` | `"settings"` | attributes | none |
| `layer` | `doc.Layers` | `Layer.Id` | attributes | parent layer (`parent`), linetype, material |
| `object` | `doc.Objects` (model and page space) | `RhinoObject.Id` | geometry, attributes, userdata (optional) | layer (`layer`, exactly one), material, linetype, group (one per group), blockDefinition (instances), dimensionStyle, hatchPattern |
| `object` (block member) | `InstanceDefinition.GetObjects()` | object id | geometry, attributes | `owner` → blockDefinition, plus the above **(open)** |
| `blockDefinition` | `doc.InstanceDefinitions` | `InstanceDefinition.Id` | attributes (name, base point, description, link info) | members are found through their `owner` ref |
| `material` | Rhino 8 render content | content id | payload (XML), attributes | none |
| `linetype`, `dimensionStyle`, `hatchPattern`, `group` | their tables | component `Id` | attributes | none |
| `note` | not a Rhino object: notes, specs, agent memos | new GUID | attributes (`title`, `markdown`) | none |

Rules:

- **Ids** are lowercase GUID strings: `id.ToString("D")`.
- **Indices become GUIDs.** `ObjectAttributes` stores layer, material and linetype by table index and groups by index (`GetGroupList()`); hatches use a pattern index. Indices differ between documents and branches, so write GUIDs into attributes and refs, and resolve them back to indices on checkout.
- **Every reference the server must check goes into `refs`**, including references hidden inside opaque geometry (`InstanceReferenceGeometry.ParentIdefId`, `AnnotationBase.DimensionStyleId`).
- **`subtype`** is the geometry class name (`Brep`, `Extrusion`, `Mesh`, `SubD`, `NurbsCurve`, `InstanceReferenceGeometry`, `TextEntity`, …).

## 3. Attribute JSON (schema proposal, client-owned)

The server reads only `layer.name`. Everything else is for clients and diffs, so keep it explicit, stable and free of session state (selection, highlight). The SDK serializes inline `attributes` canonically for you.

```json
// settings
{ "unitSystem": "Millimeters", "absoluteTolerance": 0.001, "angleToleranceRadians": 0.0174533,
  "relativeTolerance": 0.01, "userText": { "project": "Tower" } }

// layer (parent, linetype and material go into refs as GUIDs)
{ "name": "Walls", "color": "#C0392B", "visible": true, "locked": false, "userText": {} }

// object
{ "name": "Panel 01", "colorSource": "layer", "color": "#000000", "materialSource": "layer",
  "linetypeSource": "layer", "plotColorSource": "layer", "plotWeightSource": "layer",
  "visible": true, "mode": "normal", "displayOrder": 0, "space": "model", "userText": { "ifc.class": "IfcPlate" } }
```

Layer order (`SortIndex`), hidden/locked state and display order are included here but may move to session state **(open)**.

## 4. Geometry bytes

```csharp
var options = new SerializationOptions
{
    RhinoVersion = 8,            // pin it; never let it float with the running Rhino
    WriteRenderMeshes = false,   // Rhino 8.4+: keeps blobs small and hashes independent of mesh settings
    WriteAnalysisMeshes = false,
    WriteUserData = false,       // plugin user data goes to a separate "userdata" component, if at all
};
byte[] geometry = Encoding.UTF8.GetBytes(rhinoObject.Geometry.ToJSON(options));
// read back: (GeometryBase)CommonObject.FromJSON(Encoding.UTF8.GetString(bytes))
```

For the prototype the component stores the UTF-8 `ToJSON` text unchanged; it carries the archive version needed to decode it and rhino3dm reads it too. A binary envelope is a later optimization **(open)**.

**Never re-serialize an unchanged object.** Different openNURBS versions write different bytes for the same geometry, so a Rhino service release would otherwise mark untouched objects as modified. Keep the base map (section 5) and only serialize dirty entities.

## 5. Working copy state

Store per document:

- In `RhinoDoc.Strings`: `rhinotrunk.server`, `rhinotrunk.repo`, `rhinotrunk.branch`, `rhinotrunk.base` (the commit the document was last checked out at or committed as).
- A **base map** `(kind, id) → version` plus the base manifests (cache them in a sidecar folder next to the `.3dm`, e.g. `Tower.3dm.rhinotrunk/base.json`, or refetch with `ListEntitiesAsync(repo, base, includeManifests: true)`).
- In memory only: `object id → RuntimeSerialNumber` at the time the base was recorded. Rhino assigns a new runtime serial number when it replaces an object, so comparing serial numbers finds candidates for changes without serializing anything. Confirm it also changes for attribute-only and user-data edits (spike); otherwise add `ModifyObjectAttributes` and table events.

## 6. Flows

**Status** (fast, no network): objects not in the base map → added; base entries missing from the document → deleted; serial number changed → serialize, hash, and compare component hashes with the base manifest → modified or unchanged. Tables are small: re-serialize all layers, materials, etc. each time.

**Commit:**

1. Build changes for added, modified and deleted entities (inline `attributes`, `components.geometry` hash, `refs`).
2. `UploadAsync(repo, blobs)` sends only what the server lacks.
3. `CommitAsync(repo, new CommitRequest { Branch, ExpectedHead = base, Author = { Kind = "human" }, Changes })`.
4. Success: set `base` to the new commit, update the base map from `response.Versions`, record current serial numbers.
5. `409 ref_conflict`: someone else committed. Offer "update": merge your commit later, or commit to a new branch and merge.
6. `422 validation_failed`: show `error.Errors` (each names kind and id). `missing_ref` usually means a partial selection left out a new layer: add the dependency and retry.

**Checkout** (into an empty document): `ListEntitiesAsync(repo, rev, includeManifests: true)`, `BatchGetAsync` attributes and geometry in chunks of 1000, then create in dependency order: settings, linetypes, materials, layers (parents first), groups, block definitions with members, objects. Set `ObjectAttributes.ObjectId` and `Layer.Id` so Rhino keeps the entity ids, then record the base map.

**Graph panel:** `GraphAsync(repo, limit)` returns commits newest first with parents and `generation`; assign lanes in one pass. `refs` gives branch and tag labels; `author.kind` distinguishes humans and agents.

**Diff view:** `DiffAsync(repo, from, to)` lists entity changes and which components changed; fetch both geometry versions only for objects whose `geometry` changed and draw them in a display conduit (old ghosted, new highlighted).

**Merge:** `MergeAsync(repo, new MergeRequest { Branch = "main", ExpectedHead, Theirs = "agent/x", Author })`. On `409 merge_conflict` list `error.Errors` (entity + `fields`), let the user pick ours or theirs per entity, and retry with `Resolutions`. Then update the document to the new head.

## 7. Previews

Recipe `preview-mesh.v1`: for each new geometry hash without a preview (`GetDerivedAsync` returns null), mesh the object coarsely, join the meshes, serialize the mesh with `ToJSON` (same options), upload it and call `SetDerivedAsync(repo, geometryHash, "preview-mesh.v1", meshHash)`. Viewing a commit without checking it out then needs only preview blobs, cached locally by hash forever. A GLB format for web viewers is **(open)**.

## 8. Agents

Agents can use the same SDK, or plain HTTP (`samples/demo.sh`). Set `author.kind = "agent"` and put model, session and tool into `meta`. Agents should commit to their own branch (`agent/<task>`) and let a human merge. Notes (`kind: note`) and attribute edits need no geometry library; agents that create geometry without Rhino can produce a compatible JSON payload with rhino3dm (`CommonObject.Encode()` in Python, `encode()` in JavaScript), which `CommonObject.FromJSON` reads.
