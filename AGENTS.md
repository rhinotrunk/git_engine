# AGENTS.md

How AI agents work with the RhinoTrunk engine **headlessly**: over HTTP, with no Rhino UI and no human at the keyboard. Humans: see [README.md](README.md).

There are two ways in:

| | When | How |
| --- | --- | --- |
| **A. HTTP only** | Commits of notes and attributes, branching, reviews, merges; geometry made with `rhino3dm` | Plain JSON over HTTP (stdlib is enough). This page. |
| **B. Inside a Rhino session** | You need RhinoCommon (modelling, meshing, the display conduit for a human to look at) | The `rhino/rhinotrunk.py` module, driven by script globals (section B). |

The full API reference is [docs/API.md](docs/API.md). The rules below are enforced by the server; [docs/BACKEND_CHANGES.md](docs/BACKEND_CHANGES.md) explains why they exist.

## Rules for agents

1. **Identify as an agent** on every write: `"author": {"id": "agent:<name>", "name": "...", "kind": "agent"}`. Put model, session and tool in commit `meta` (`agent.model`, `agent.session`, `agent.tool`).
2. **Never commit to a protected branch** (`main` by default; see `GET /repos/{repo}/policy`). The server returns 403 `forbidden`. Work on your own branch, `agent/<task>`.
3. **You may merge freely only into branches you created.** Merging into `main`, into another author's branch, or any merge that merge rules had to resolve needs a **human approval**.
4. **Never create approvals.** `POST /approvals` is for humans, and the server rejects `kind: agent`. Do not pose as a human: identity is self-declared until auth exists, so this rule is on you.
5. **On 403 `approval_required`, stop.** Leave a merge request (an annotation, below), report it, and wait. Do not retry in a loop.
6. **Always preview before merging** (`POST /merges/preview`). Merge with the commit hashes the preview returned (`expectedHead = preview.ours`, `theirs = preview.theirs`), so you merge exactly what was checked.
7. **Use compare-and-swap.** Every commit sends `expectedHead` (the head you built on). On 409 `ref_conflict`, re-read, rebuild your change and retry. Never force.
8. **Never re-upload or re-serialize unchanged geometry.** Send only what changed; reuse existing versions with `{"op": "put", "version": "<manifest hash>"}`.

## Finding the server

- Default: `http://localhost:5280`. `GET /health` returns `{"status":"ok"}` and `GET /` lists every route.
- It can also run on a LAN address (see README); use the URL you were given.
- Start one if needed: `dotnet run --project src/RhinoTrunk.Server`. A throwaway in-memory server: `Storage__Provider=InMemory dotnet run --project src/RhinoTrunk.Server --urls http://localhost:5299`.
- The server has **no authentication**. Treat every repository as shared with everyone who can reach the server.

## A. HTTP only

### Core loop

| Step | Call |
| --- | --- |
| 1. Find the branch head | `GET /repos/{repo}/branches/{name}` (or `/refs/branches/{name}`) |
| 2. Branch | `POST /repos/{repo}/branches {name: "agent/<task>", from: "main", author}`; `parent` defaults to `from` |
| 3. Read what you need | `GET /commits/{rev}/entities?kind=&manifests=true`, `POST /objects/batch-get` |
| 4. Upload new blobs | `POST /objects/missing`, then `PUT /objects/{hash}` for each missing hash |
| 5. Commit | `POST /repos/{repo}/commits {branch, expectedHead, message, author, meta, changes}` |
| 6. Preview the merge | `POST /merges/preview {branch: "main", theirs: "agent/<task>", author}` |
| 7a. No approval needed | `POST /merges {branch, expectedHead: preview.ours, theirs: preview.theirs, author}` |
| 7b. Approval needed | Post a merge request, stop. Later: `GET /approvals?branch=main&theirs=agent/<task>`, then merge with `approval: <id>` |

Revisions are a commit hash, branch name or tag name. Hashes are `sha256:<64 hex>` over the exact bytes. In URL path segments, encode `/` in branch names as `%2F`, except under `/branches/` and `/refs/branches/`, which take the raw name.

### Worked example (Python stdlib)

```python
import base64, hashlib, json, urllib.request, urllib.error, uuid

BASE, REPO = "http://localhost:5280", "pavilion-options"
ME = {"id": "agent:example-bot", "name": "Example bot", "kind": "agent"}

def call(method, path, body=None, raw=None):
    data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
    headers = {"content-type": "application/octet-stream" if raw is not None else "application/json"}
    req = urllib.request.Request(BASE + path, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req) as r:
            text = r.read()
            return json.loads(text) if text else None
    except urllib.error.HTTPError as e:
        err = json.loads(e.read() or b"{}")
        raise RuntimeError(f"{e.code} {err.get('code')}: {err.get('message')} {err.get('data') or ''}")

def sha(b): return "sha256:" + hashlib.sha256(b).hexdigest()

r = f"/repos/{REPO}"
branch = "agent/example-" + uuid.uuid4().hex[:6]

# 1-2: branch off main (records parent=main and you as the creator)
head = call("POST", r + "/branches", {"name": branch, "from": "main", "author": ME})["commit"]

# 3-4: new geometry would be uploaded like this (hash -> bytes); this example needs none
def upload(blobs):
    missing = call("POST", r + "/objects/missing", {"hashes": list(blobs)})["missing"]
    for h in missing:
        call("PUT", r + f"/objects/{h}", raw=blobs[h])

# 5: commit a note, and rename an existing object (attribute-only edit: keep its geometry and refs)
obj = call("GET", r + f"/commits/{head}/entities?kind=object&manifests=true")["entities"][0]
m = obj["manifest"]
blob = call("POST", r + "/objects/batch-get", {"hashes": [m["components"]["attributes"]]})["objects"]
attrs = json.loads(base64.b64decode(blob[m["components"]["attributes"]]))
attrs["name"] = (attrs.get("name") or "Object") + " (reviewed)"
changes = [
    {"op": "put", "kind": "note", "id": str(uuid.uuid4()),
     "manifest": {"attributes": {"title": "Study", "markdown": "Tried a lighter roof."}}},
    {"op": "put", "kind": "object", "id": obj["id"],
     "manifest": {"subtype": m.get("subtype"), "attributes": attrs,
                  "components": {"geometry": m["components"]["geometry"]}, "refs": m["refs"]}},
]
res = call("POST", r + "/commits", {"branch": branch, "expectedHead": head, "message": "Add study note",
                                     "author": ME, "meta": {"agent.tool": "example"}, "changes": changes})
print("committed", res["commit"])

# 6: preview the merge into main
p = call("POST", r + "/merges/preview", {"branch": "main", "theirs": branch, "author": ME})
print(p["status"], p["incoming"], "conflicts:", len(p["conflicts"]), "approval:", p["approvalReasons"])

# 7: merge, or request approval and stop
if p["requiresApproval"]:
    approvals = [a for a in call("GET", r + "/approvals?branch=main")["approvals"]
                 if a["status"] == "active" and a["theirs"] == p["theirs"]]
    if not approvals:
        call("POST", r + f"/commits/{p['theirs']}/annotations", {
            "namespace": "review/merge-request", "status": "pending", "author": ME,
            "summary": f"Merge {branch} into main", "data": {"into": "main", "theirs": branch}})
        print("approval required; merge request posted -> stop")
    else:
        call("POST", r + "/merges", {"branch": "main", "expectedHead": p["ours"], "theirs": p["theirs"],
                                      "author": ME, "approval": approvals[0]["id"]})
elif p["canMerge"]:
    call("POST", r + "/merges", {"branch": "main", "expectedHead": p["ours"], "theirs": p["theirs"], "author": ME})
```

`batch-get` returns blobs as **base64**: decode with `base64.b64decode(value)`. Attribute blobs are UTF-8 canonical JSON.

### Change shapes

```jsonc
// new or modified object (attributes inline; the server stores them as a blob)
{"op": "put", "kind": "object", "id": "<lowercase guid>",
 "manifest": {"subtype": "Brep",
              "attributes": {"name": "Panel 01", "colorSource": "layer", "color": "#000000", "visible": true,
                             "mode": "normal", "userText": {}},
              "components": {"geometry": "sha256:<uploaded blob>"},
              "refs": [{"kind": "layer", "id": "<layer guid>", "role": "layer"}]}}

// attribute-only edit: keep the geometry hash from the current manifest
// layer (parent optional)
{"op": "put", "kind": "layer", "id": "<guid>",
 "manifest": {"attributes": {"name": "Study", "color": "#2e86c1", "visible": true, "locked": false},
              "refs": [{"kind": "layer", "id": "<parent guid>", "role": "parent"}]}}

// restore an existing version, or delete
{"op": "put", "kind": "object", "id": "<guid>", "version": "sha256:<manifest hash>"}
{"op": "delete", "kind": "object", "id": "<guid>"}
```

- Every object needs exactly one `layer` ref.
- Sibling layer names must be unique.
- A layer cannot be deleted while anything refers to it.
- The server returns 422 `validation_failed` with per-entity details when a snapshot breaks these rules. See the invariants in API.md.

### Geometry without Rhino

- **Encoding:** geometry blobs are the UTF-8 JSON of Rhino's `CommonObject.ToJSON`. With `rhino3dm` (Python), `json.dumps(geometry.Encode()).encode("utf-8")` produces a compatible blob, which Rhino reads with `CommonObject.FromJSON`.
- **Hash** the exact bytes you upload.
- **Previews:** register a coarse mesh for new geometry so humans can review it in Rhino without the real geometry: upload the mesh JSON, then `PUT /derived/{geometryHash}/preview-mesh.v1 {"result": "<mesh hash>"}`. Skip curves and points.

### Reviewing and merging

| Need | Call |
| --- | --- |
| What changed between two revisions | `GET /diff?from=&to=&manifests=true` |
| Common ancestor | `GET /merge-base?a=&b=` |
| History of one entity | `GET /history/{kind}/{id}?rev=` |
| Branches, their parents and creators | `GET /branches` |
| Dry-run merge, into a head or any point (`ours`) | `POST /merges/preview` |

**Conflicts.** The preview lists them with the base, ours and theirs versions and the conflicting fields.

- In a merge into **your own** branch, resolve them yourself with `resolutions: [{kind, id, take: "ours"|"theirs"}]`.
- In a merge into anything else, report them and let a human decide.

**Merge rules** (`policy.mergeRules`) may resolve conflicts automatically. Such merges are flagged `rules_applied` and always need a human approval.

**Checks.** Attach CI-style results to a commit with `POST /commits/{rev}/annotations`, using namespaces such as `ci/geometry-valid` with `pending`, `success` or `failure`. Annotations never change the commit id.

## B. Inside a Rhino session

Use this when you need RhinoCommon, e.g. through an MCP `run_python` tool connected to Rhino 8.

**Do not depend on the Rhino command or alias names.** The aliases (`ge_commit`, …) and command names (`TrunkCommit`, …) are for humans and will change when the C# plugin replaces the Python prototype ([docs/CSHARP_CONVERSION_GUIDE.md](docs/CSHARP_CONVERSION_GUIDE.md)). The stable agent interface is the module plus these globals:

```python
RT_HOME = r"<repo>\rhino"
RT_AUTHOR = {"id": "agent:massing-bot", "name": "Massing bot", "kind": "agent"}
RT_CMD = "pull"                       # commit | pull | branch | merge | compare | status | off
RT_ARGS = {"rev": "main", "repo": "pavilion-options", "force": True}
exec(open(RT_HOME + r"\rhinotrunk.py", encoding="utf-8").read(), globals())
print(RT_RESULT)                      # {"status": ..., "log": [...], ...}; never prompts in agent mode
```

| `RT_CMD` | `RT_ARGS` | Notes |
| --- | --- | --- |
| `status` | none | Local changes against the file's commit |
| `pull` | `rev`, `force`, `repo` | Loads only differences. `force=True` is required to discard local changes. |
| `branch` | `name`, `commit`, `message` | Branches from the file's commit; the file switches to it |
| `commit` | `message`, `path` (Save As target for a never-saved file) | Saves the file first. Refused on protected branches. |
| `merge` | `into`, `theirs`, `resolutions`, `switch` | Stops with `status: "approval_required"` and posts a merge request when a human must approve |
| `compare` | `options`, `at`, `branch`, `mode` (`cycle`, `overlay`, `side`), `show`, `bench` | Leaves the preview on screen for a human |
| `off` | none | Hides any preview |

Check `RT_RESULT["status"]`: `error` (with `error.code`), `cancelled`, `approval_required`, `conflicts` (with the list), or the command's success status.

Calling `RhinoApp.RunScript("ge_…")` from inside a running script can hang Rhino; use the globals above instead.

## Errors you will see

| Code | Meaning for an agent |
| --- | --- |
| 409 `ref_conflict` | Someone moved the branch. Re-read the head, rebuild, retry. |
| 403 `forbidden` | You tried something only humans may do (commit or move `main`, approve, change the policy). |
| 403 `approval_required` | Stop and request approval. `data.reasons` says why. |
| 409 `approval_stale` | A head moved after the human approved. Ask again; do not reuse. |
| 409 `merge_conflict` | Preview first; resolve (own branch) or report. |
| 422 `validation_failed` | Your snapshot breaks an invariant; `errors[]` names the entity. |
| 422 `object_missing` | Upload blobs before committing. |
| 422 `no_changes` | Nothing to commit. |
