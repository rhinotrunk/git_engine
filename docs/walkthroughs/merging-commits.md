# How a client merges commits

A merge takes two lines of history and produces one snapshot on a branch. The usual case is a human on `main` bringing in an agent branch such as `agent/study`.

Related: [committing a document](committing-a-document.md), [checking out a commit](checking-out-a-commit.md), [comparing commits](comparing-commits.md).

The client does not build the combined document itself. It names the branch to update (`branch`, “ours”) and the other side (`theirs`, a commit, branch, or tag), plus the head it expects the branch to be on. The server does the rest.

## 1. The client sends a merge request

```text
branch:        main
expectedHead:  the commit the client believes main points at
theirs:        agent/study
author:        who is merging
message:       optional
resolutions:   optional, filled in only after a conflict
```

`expectedHead` works the same way as on a normal commit. If `main` has moved, the server returns **409 `ref_conflict`** and leaves the branch alone.

## 2. The server picks one of three outcomes

It locks the repository, reads the branch, and resolves `theirs` to a commit. Then it looks at how the two commits are related.

**Already included.** Every commit on `theirs` is already in the history of `main`. The branch does not move. The response status is `upToDate`, and the commit id is the current head of `main`.

**Fast-forward.** `main` is a direct ancestor of `theirs`: the other side only added commits on top. The server slides the branch pointer forward to `theirs`. No new commit is created. The response status is `fastForward`. The client can ask to skip this shortcut with `noFastForward`, which forces a merge commit even when a slide would do.

**Real merge.** Both sides have commits the other does not. The server builds a new commit with two parents: `main`, then `theirs`. The response status is `merged`.

## 3. How a real merge combines the document

The server finds the **merge base**: the latest commit that both sides share. That snapshot is the common starting point.

It then lists what each side changed since that base, entity by entity (one layer, one object, one material, …).

- Only `theirs` touched an entity: take their version, including a delete.
- Both sides made the same change (the version hashes match): nothing to do. `main` already has it.
- Both sides edited the same entity, and the edits touch different fields: combine them. Geometry on one side and attributes on the other become one manifest. The same rule applies to subtype, refs, and each named component (`geometry`, `attributes`, `userdata`, …). A field that only one side changed wins. A field both sides changed to the same value is kept.
- Both sides changed the same field to different values, or one side deleted the entity while the other edited it, or both sides added the same id with different content: that entity is a **conflict**. The merge stops. Nothing is written to the branch.

## 4. When there is a conflict

The server returns **409 `merge_conflict`**. The body lists each conflicting entity (kind and id) and, when the clash is inside a manifest, the field names (`components.geometry`, `components.attributes`, `refs`, `subtype`). It also returns the three commit ids: merge base, ours, and theirs.

The client shows that list and lets the person choose, per entity, **ours** or **theirs**. It sends the same request again with `resolutions`:

```text
{ kind: "object", id: "e93a…", take: "theirs" }
```

`ours` means “keep what `main` already has,” so that entity is left out of the applied changes. `theirs` means “replace it with their version.” The choice is for the whole entity. There is no field-by-field pick in this prototype. Another way through is to commit a fix on either branch and merge again.

## 5. The merged snapshot is checked like any commit

After the entities are combined, the server runs the same checks as a normal commit: references must exist, deleted things must not still be referenced, every object needs exactly one layer, and the layer tree must stay valid.

That catches combinations that are each fine on their own. Example: one side deletes a layer, the other adds an object on that layer. The merge can assemble that snapshot and then reject it with **422 `validation_failed`**. Fix it with a commit on one of the branches, then merge again.

## 6. The branch moves

If the snapshot is valid, the server stores a merge commit:

- tree: the combined snapshot
- parents: the old `main` head, then `theirs`
- message: the one the client sent, or `Merge agent/study into main`
- generation: one past the higher of the two parents

It then moves the branch with compare-and-swap, the same last check a normal commit uses. If the branch moved during the merge, the response is **409 `ref_conflict`**.

## 7. What the client does next

The response carries the commit the branch now points at. The open document is still the old one until the client checks that commit out (or otherwise updates the document to the new head) and records it as the new base. See [checking out a commit](checking-out-a-commit.md).
