# RhinoTrunk API (prototype v0.1)

Plain JSON over HTTP. The C# SDK in `src/RhinoTrunk.Client` wraps every call; `samples/demo.sh` shows the same flow with curl.

- **Base URL:** `http://localhost:5280` when started with `dotnet run --project src/RhinoTrunk.Server`.
- **JSON:** camelCase, `null` members omitted. `GET /` lists every route.
- **Hashes:** `sha256:` + 64 lowercase hex digits, computed over the exact bytes stored.
- **Revisions (`{rev}`, `from`, `to`, `theirs`):** a commit hash, a branch name or a tag name, tried in that order. In a path segment, encode `/` in branch names as `%2F` (`agent%2Fstudy`).
- **Auth:** none yet (see [OPEN_QUESTIONS.md](OPEN_QUESTIONS.md)).

## Concepts

| Term | Meaning |
| --- | --- |
| Repository | One versioned project. Holds objects, refs, annotations, derived data. |
| Document (`doc`) | One Rhino document inside a snapshot. Always `"main"` for now. |
| Entity | One versioned thing, addressed by `(doc, kind, id)`. `id` is the Rhino GUID, lowercase; the settings singleton uses `"settings"`. |
| Kind | `settings`, `layer`, `object`, `blockDefinition`, `material`, `linetype`, `dimensionStyle`, `hatchPattern`, `group`, `namedView`, `note`. |
| Manifest | One immutable version of an entity: `kind`, optional `subtype`, named `components` (blob hashes) and `refs` to other entities. Contains no entity id, so identical copies share one version. |
| Version | The hash of a manifest. |
| Component | A blob referenced by a manifest: `geometry` (opaque bytes), `attributes` (canonical JSON), `userdata`, `payload`, ... |
| Commit | Snapshot root hash, parents (0, 1 or 2), generation, author (`kind: human | agent`), timestamp, message, `meta`. Its id is its hash. |
| Branch / tag | Mutable pointer to a commit, moved only by compare-and-swap. Tags never move. |
| Annotation | Mutable note about a commit (CI result, review). Never changes the commit id. |
| Derived record | `(source hash, recipe) → result hash`, e.g. a preview mesh for a geometry blob. |

## Errors

Every failure returns this shape:

```json
{
  "code": "validation_failed",
  "message": "The resulting snapshot violates document invariants.",
  "errors": [
    { "code": "dangling_ref", "message": "Cannot delete layer 6b1f…: still referenced by object e93a… (layer).",
      "doc": "main", "kind": "object", "id": "e93a…" }
  ],
  "data": { "current": "sha256:…" }
}
```

| HTTP | `code` | When |
| --- | --- | --- |
| 400 | `invalid_request` | Malformed JSON or request (details name the bad change). |
| 400 | `hash_mismatch` | Uploaded bytes do not hash to the URL's hash (`data.actual`). |
| 404 | `not_found` | Repository, commit, ref, entity or object does not exist. |
| 409 | `ref_conflict` | `expectedHead` / `expected` is stale. `data.current` holds the actual head. Fetch, merge or rebase, retry. |
| 409 | `repo_exists`, `tag_immutable` | As named. |
| 409 | `merge_conflict` | Entities changed incompatibly on both sides; `errors[].fields` lists them. |
| 422 | `validation_failed` | Snapshot would break an invariant (table below). |
| 422 | `no_changes` | The commit would not change the snapshot. |
| 422 | `object_missing` | A referenced blob has not been uploaded. |

### Invariants checked on every commit and merge

| Detail code | Rule |
| --- | --- |
| `object_missing` | Every component hash was uploaded first. |
| `kind_mismatch`, `invalid_manifest`, `unknown_kind`, `invalid_entity_id` | Manifest is well formed. |
| `missing_ref` | Every `refs` target exists in the same document of the new snapshot. |
| `dangling_ref` | A deleted entity is not referenced by anything left. |
| `object_without_layer` | Every object references exactly one layer (role `layer`). |
| `settings_count` | Exactly one `settings` entity per document. |
| `no_layers` | At least one layer per document. |
| `invalid_layer_name` | Layers have an `attributes` component with a non-empty `name` without `::`. |
| `duplicate_layer_name` | Sibling layer names are unique, ignoring case. |
| `layer_cycle` | Layer parent chains terminate. |

Rhino validity (geometry `IsValid`, meshing) is not checked by the server; see [CLIENT_GUIDE.md](CLIENT_GUIDE.md).

## Repositories

| Call | Body → Response |
| --- | --- |
| `POST /repos` | `{ "name": "tower" }` → 201 `{ "name", "created" }` |
| `GET /repos` | → `{ "repos": [ … ] }` |
| `GET /repos/{repo}` | → `{ "name", "created" }` |

## Objects (blobs, manifests)

| Call | Body → Response |
| --- | --- |
| `POST /repos/{repo}/objects/missing` | `{ "hashes": [ … ] }` (max 1000) → `{ "missing": [ … ] }` |
| `PUT /repos/{repo}/objects/{hash}` | raw bytes → 201 (new) or 200 (existed) `{ "hash", "created", "size" }` |
| `GET /repos/{repo}/objects/{hash}` | → raw bytes |
| `POST /repos/{repo}/objects/batch-get` | `{ "hashes": [ … ] }` (max 1000) → `{ "objects": { hash: base64 }, "missing": [ … ] }` |

Upload flow: hash locally, ask `missing`, `PUT` only those. Uploads are idempotent.

## Commits

`POST /repos/{repo}/commits`

```json
{
  "branch": "main",
  "expectedHead": "sha256:a1…",
  "message": "Panelize south facade",
  "author": { "id": "agent:facade-bot", "name": "Facade bot", "kind": "agent" },
  "meta": { "agent.model": "…", "agent.session": "…" },
  "changes": [
    { "op": "put", "kind": "layer", "id": "6b1f…",
      "manifest": { "attributes": { "name": "Facade", "color": "#2e86c1" },
                    "refs": [ { "kind": "layer", "id": "0c3e…", "role": "parent" } ] } },
    { "op": "put", "kind": "object", "id": "e93a…",
      "manifest": { "subtype": "Brep",
                    "attributes": { "name": "Panel 01" },
                    "components": { "geometry": "sha256:9f2c…" },
                    "refs": [ { "kind": "layer", "id": "6b1f…", "role": "layer" } ] } },
    { "op": "put", "kind": "object", "id": "77aa…", "version": "sha256:<an older manifest>" },
    { "op": "delete", "kind": "object", "id": "17c0…" }
  ]
}
```

- `expectedHead` is the branch head the changes are based on; `null` creates the branch with its first commit. A mismatch returns 409 `ref_conflict`.
- `doc` defaults to `"main"`. `manifest.kind` defaults to the change's kind.
- `attributes` given inline are stored by the server as a canonical JSON blob and referenced as component `attributes`. Alternatively upload canonical JSON yourself and pass its hash in `components.attributes`.
- `version` instead of `manifest` re-uses an existing manifest (restore, cherry-pick).
- Response 201:

```json
{ "commit": "sha256:…", "tree": "sha256:…", "branch": "main",
  "versions": [ { "doc": "main", "kind": "layer", "id": "6b1f…", "version": "sha256:…" } ] }
```

Keep `versions`: they are the client's new base for those entities.

| Call | Response |
| --- | --- |
| `GET /repos/{repo}/commits/{rev}` | `{ "id", "tree", "parents", "generation", "author", "timestamp", "message", "meta" }` |
| `GET /repos/{repo}/commits/{rev}/entities?doc=&kind=&manifests=true` | `{ "commit", "entities": [ { "doc", "kind", "id", "version", "manifest"? } ] }` |
| `GET /repos/{repo}/commits/{rev}/entities/{kind}/{id}?doc=` | one entity with its manifest |

## Refs

| Call | Body → Response |
| --- | --- |
| `GET /repos/{repo}/refs` | → `{ "refs": [ { "name", "type": "branch" \| "tag", "commit" } ] }` |
| `GET /repos/{repo}/refs/branches/{name}` | → ref (names may contain `/`, unencoded here) |
| `PUT /repos/{repo}/refs/branches/{name}` | `{ "commit", "expected": null \| "sha256:…" }` → ref. `expected: null` creates. |
| `DELETE /repos/{repo}/refs/branches/{name}?expected=sha256:…` | → 204 |
| `PUT /repos/{repo}/refs/tags/{name}` | `{ "commit" }` → ref. Tags cannot be moved. |

## Diff, graph, history

`GET /repos/{repo}/diff?from={rev}&to={rev}` (omit `from` for "everything in `to`"):

```json
{ "from": "sha256:…", "to": "sha256:…",
  "changes": [
    { "doc": "main", "kind": "object", "id": "e93a…", "change": "modified",
      "from": "sha256:…", "to": "sha256:…",
      "components": [ "geometry" ], "refsChanged": true, "subtypeChanged": false },
    { "doc": "main", "kind": "layer", "id": "6b1f…", "change": "added", "to": "sha256:…" } ] }
```

Cost follows the size of the change: unchanged subtrees are skipped by hash, and no geometry is read.

`GET /repos/{repo}/graph?limit=500&ref=main&ref=agent/study` (no `ref` = all refs): commits newest first, every commit before its parents, with `generation` for lane layout and `refs` labels (`"main"`, `"tag:v1"`). `truncated: true` when more exist.

`GET /repos/{repo}/history/{kind}/{id}?rev=main&doc=main&limit=100`: first-parent history of one entity (`added`, `modified`, `deleted`).

## Merges

`POST /repos/{repo}/merges`

```json
{ "branch": "main", "expectedHead": "sha256:…", "theirs": "agent/study",
  "author": { "id": "alex", "kind": "human" }, "message": "optional",
  "noFastForward": false,
  "resolutions": [ { "kind": "object", "id": "e93a…", "take": "theirs" } ] }
```

Response: `{ "status": "merged" | "fastForward" | "upToDate", "commit", "mergeBase", "branch" }`.

The merge is three-way per entity, then per manifest field (`subtype`, `refs`, each component). A geometry edit on one side and an attribute edit on the other combine automatically. Same field changed differently → 409 `merge_conflict`:

```json
{ "code": "merge_conflict", "message": "1 conflicting entities. …",
  "data": { "mergeBase": "sha256:…", "ours": "sha256:…", "theirs": "sha256:…" },
  "errors": [ { "code": "conflict", "kind": "object", "id": "e93a…",
                "message": "Both sides changed the same fields.", "fields": [ "components.attributes" ] } ] }
```

Resolve with `resolutions` (`ours` or `theirs` per entity), or commit a fix on either branch and merge again. The merged snapshot is validated like any commit (422 on, for example, an object added on a layer the other side deleted).

## Annotations and derived data

| Call | Body → Response |
| --- | --- |
| `POST /repos/{repo}/commits/{rev}/annotations` | `{ "namespace": "ci/geometry-valid", "status": "pending\|success\|failure\|info", "summary", "data": {…}, "author" }` → stored annotation with `timestamp` |
| `GET /repos/{repo}/commits/{rev}/annotations` | → `{ "commit", "annotations": [ … ] }` |
| `PUT /repos/{repo}/derived/{sourceHash}/{recipe}` | `{ "result": "sha256:…" }` (result uploaded first) → record |
| `GET /repos/{repo}/derived/{sourceHash}/{recipe}` | → `{ "source", "recipe", "result" }` or 404 |

## Limits (prototype)

- Request bodies up to 512 MB (`Limits:MaxRequestBodyMegabytes`).
- `missing` and `batch-get`: 1000 hashes per call. Entity lists are not paged yet.
- Storage: `Storage:Provider` = `FileSystem` (default, directory `Storage:Root`, default `data/`) or `InMemory`. One server process per data directory.
