# How a client commits a document

A client commits a Rhino document by sending only what changed since the last known commit, then asking the server to attach those changes to a branch.

Related: [merging commits](merging-commits.md), [checking out a commit](checking-out-a-commit.md), [comparing commits](comparing-commits.md).

## Before the commit

The plugin remembers four facts about the open document: which server, which repository, which branch, and the **base**. The base is the commit this document was last checked out from or last committed as.

It also keeps a **base map**: for every layer, object, material, and so on, the version that was in that commit. Unchanged objects are not serialized again. A newer Rhino build would otherwise rewrite the same geometry as different bytes and look like an edit.

## 1. Find what changed

This step stays on the machine. No network yet.

- An object that is in the document and missing from the base map is **added**.
- An entry in the base map that is gone from the document is **deleted**.
- If Rhino’s runtime serial number for an object has changed, the plugin serializes it, hashes the geometry, and compares that hash with the base. A different hash means **modified**. The same hash means unchanged, so that object is left out of the commit.
- Layers, materials, and the other small tables are re-serialized every time. They are cheap.

## 2. Turn the edits into a change list

Each changed thing becomes one change, identified by its kind and its Rhino GUID (lowercase).

- **Put** for an add or a modification. The payload is a manifest: attribute JSON (name, color, visibility), a hash of the geometry bytes, and **refs** (which layer it sits on, which material, parent layer, and so on).
- **Delete** for something removed. A delete carries no content, only the id.

The document id is `"main"` in this prototype.

## 3. Upload the bytes the server does not have

Geometry is stored as opaque blobs addressed by SHA-256. The client hashes those blobs locally, asks the server which hashes are missing, and uploads only those. Uploading the same bytes again is harmless.

Attribute JSON can travel inside the commit itself. The server turns it into a blob and records the hash. The client can leave attribute upload to the server.

## 4. Send the commit

The client posts to the repository with:

- the branch name, for example `main`
- `expectedHead`, which is the base commit (`null` if this is the first commit on a new branch)
- the author (`human` or `agent`)
- a message
- the change list

`expectedHead` means “I built this on top of this commit.” If the branch has moved, the server refuses the commit so one writer cannot overwrite another.

## 5. What the server does with that request

1. It checks the request is well formed: known kinds, valid ids, each entity listed once, each put has either a manifest or an existing version hash.
2. It locks the repository and reads the branch. If the branch head is not `expectedHead`, it returns **409**. The branch stays where it was.
3. For each put it stores an immutable **manifest** and gets back a version hash. Deletes are marked as “this id is gone.”
4. It copies the previous snapshot’s tree and applies only those changes. The tree is nested: document, then kind (layer, object, …), then shards, then the id-to-version entries. Unchanged parts keep their old hashes.
5. If the new tree hash equals the old one, it returns **422** (`no_changes`). A commit that changes nothing is not stored.
6. It checks the new snapshot: every reference points at something that exists, deleted things are not still referenced, every object has exactly one layer, there is exactly one settings entity and at least one layer, sibling layer names are unique, and layer parents do not form a cycle. Geometry validity is a client concern. A failure is **422**, and each error names the kind and id.
7. If that passes, it stores a **commit** object: the new tree, the previous commit as parent, author, time, and message. The commit’s id is the hash of that object.
8. It moves the branch pointer from the old head to this commit, and only if the pointer is still the old head. That compare-and-swap is the last check. If another writer won the race, the server returns **409**.

## 6. After a successful commit

The response contains the new commit id and a `versions` list: the new version hash for each entity that was put. The plugin then:

- sets the document’s base to that commit id
- updates the base map from `versions`
- records the current runtime serial numbers

The next status check then shows a clean document.

## When it fails

- **409 `ref_conflict`**: someone else committed to the branch. The client can update (merge) and try again, or commit onto a new branch and merge later. `data.current` is the real head.
- **422 `validation_failed`**: the snapshot would be inconsistent. A typical case is `missing_ref`: a new object points at a new layer that was not included. Add the missing entity and retry.
- **422 `object_missing`**: a geometry hash in the change list was never uploaded.
