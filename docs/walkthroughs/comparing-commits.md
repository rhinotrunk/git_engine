# How a client compares commits

Comparing two commits lists what changed in the document between them: which entities were added, deleted, or modified, and which parts of a modified entity differ.

Related: [committing a document](committing-a-document.md), [checking out a commit](checking-out-a-commit.md), [merging commits](merging-commits.md).

The client calls `DiffAsync(repo, from, to)`. `from` and `to` are each a commit hash, a branch name, or a tag name. Leave `from` out to compare `to` with an empty document, which lists everything in that snapshot.

The server reads no geometry for this call. The answer is ids, version hashes, and the names of the parts that differ.

## 1. The server walks the two trees

Each commit points at a snapshot tree: document → kind → shard → entity id → version hash. Two commits that share history share most of those hashes.

The walk compares hashes from the top down:

- If a whole document, kind, or shard has the same hash on both sides, everything under it is unchanged and is skipped.
- Where a hash differs, the walk goes one level deeper.
- At an entity, it records a change when the version hash differs, or when the id exists on only one side.

So the cost follows the size of the change. A one-object edit in a large model reads a few tree nodes, not every object.

## 2. Each change is labeled

| Situation | `change` | What else is filled in |
| --- | --- | --- |
| The id exists only in `to` | `added` | `to` version |
| The id exists only in `from` | `deleted` | `from` version |
| Both sides have the id, and the version hashes differ | `modified` | both versions, plus which parts differ |

For a modification the server loads the two manifests (small JSON, still no geometry) and compares them field by field:

- `components`: the names whose hashes differ, for example `geometry`, `attributes`, `userdata`
- `refsChanged`: the reference list differs (layer, parent, material, …)
- `subtypeChanged`: the geometry class name differs (`Brep`, `Mesh`, …)

An added or deleted entity stops there. There is no second manifest to compare.

The response also echoes the resolved commit ids of `from` and `to`, so a branch name in the request becomes a concrete commit in the answer.

## 3. What the client draws

The diff list is enough for a summary: “Panel 01 changed geometry”, “layer Facade was added”, “this object was deleted.”

To draw the geometry, the client looks only at objects whose `components` include `geometry`. It downloads those two blobs (the old hash and the new hash) and draws them in a display conduit: the old shape ghosted, the new shape highlighted. Attributes and other small components can be fetched the same way when the panel needs to show the actual JSON.

Unchanged objects are absent from the list, so the client does not download them.

## 4. One entity across history

A diff is between two snapshots. To follow a single layer or object backward from a revision, the client uses history: `GET /repos/{repo}/history/{kind}/{id}?rev=main`.

The server walks first-parent history and returns the commits where that entity’s version changed, each marked `added`, `modified`, or `deleted`. The same version hashes can then be diffed or downloaded.

## Where this shows up in a merge

A merge uses the same tree walk. It diffs the merge base against each side, then combines those two lists. See [merging commits](merging-commits.md). The compare call itself never writes anything and never moves a branch.
