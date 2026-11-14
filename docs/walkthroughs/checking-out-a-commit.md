# How a client checks out a commit

Checking out loads one commit into an empty Rhino document and remembers that commit as the base for later edits.

Related: [committing a document](committing-a-document.md), [merging commits](merging-commits.md), [comparing commits](comparing-commits.md).

The server stores entities, hashes, and trees. It does not know about Rhino. The plugin asks for the snapshot, downloads the bytes, and creates the Rhino objects itself.

A revision can be a commit hash, a branch name, or a tag name. The server tries them in that order.

## 1. Read the entity list

The client calls `ListEntitiesAsync` for that revision with manifests included. The response is the full set of things in the document at that commit: settings, layers, objects, materials, and the rest. Each entry has:

- kind and id (the Rhino GUID, or `"settings"` for the settings singleton)
- a version hash (the manifest)
- the manifest itself: subtype, component hashes, and refs

Component hashes point at the actual bytes (geometry, attributes, and so on). The list call does not download those bytes yet.

## 2. Download attributes and geometry

The client collects the component hashes it needs and fetches them with `BatchGetAsync`, in chunks of 1000. Bytes are cached by hash: the same geometry shared by two commits is downloaded once.

Attributes come back as canonical JSON. Geometry comes back as the UTF-8 `ToJSON` text the client stored at commit time. The plugin turns that text back into Rhino geometry with `CommonObject.FromJSON`.

## 3. Create Rhino objects in dependency order

Some things have to exist before others can point at them. The plugin creates them in this order:

1. Settings (units, tolerances, document user text).
2. Linetypes and materials.
3. Layers, parents before children. A layer’s parent is a ref, so the parent layer is created first.
4. Groups.
5. Block definitions, including their member objects.
6. Model and page-space objects.

While creating each one, the plugin writes the stored GUID back onto it (`Layer.Id`, `ObjectAttributes.ObjectId`). Rhino then keeps the same ids the server uses. Table indices (layer index, material index) are filled in locally from those GUIDs. Indices are not stored, because they differ between documents.

Refs such as “this object is on this layer” and “this layer’s parent is that layer” are resolved to those local indices at this step.

## 4. Record the working copy

After the document exists, the plugin stores the checkout so the next commit knows what it started from:

- In `RhinoDoc.Strings`: server, repository, branch, and **base** (this commit’s id).
- A **base map** of `(kind, id) → version`, plus the manifests, kept beside the `.3dm` (for example `Tower.3dm.rhinotrunk/base.json`). The map can also be rebuilt later by listing the commit again.
- In memory only: each object id and the runtime serial number Rhino assigned at checkout. A later status check uses that number to notice which objects Rhino has replaced.

From here, [committing](committing-a-document.md) sends only what differs from this base.

## What checkout does not do

Checkout in this prototype fills an empty document. It does not patch an already open model in place.

The server does not check that geometry is valid Rhino geometry. If the bytes were accepted at commit time, checkout recreates them.

Viewing a commit without building the full document is a different path: preview meshes stored as derived data, one mesh blob per geometry hash. The graph and diff views use commits and manifests directly and do not need a Rhino document at all. See [comparing commits](comparing-commits.md).
