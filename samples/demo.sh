#!/usr/bin/env bash
# End-to-end walkthrough of the RhinoTrunk API with curl, as a non-.NET agent would use it.
#
#   dotnet run --project src/RhinoTrunk.Server      # in another terminal (listens on http://localhost:5280)
#   ./samples/demo.sh [base-url]
#
# Requires: bash, curl, python3 (only for JSON parsing, hashing and GUIDs).
set -euo pipefail

BASE=${1:-http://localhost:5280}
REPO="demo-$(date +%s)"
JSON='content-type: application/json'

jget() { python3 -c 'import sys, json
d = json.load(sys.stdin)
for k in sys.argv[1].split("."):
    d = d[int(k)] if k.isdigit() else d[k]
print(d)' "$1"; }
sha() { python3 -c 'import sys, hashlib; print("sha256:" + hashlib.sha256(open(sys.argv[1], "rb").read()).hexdigest())' "$1"; }
guid() { python3 -c 'import uuid; print(uuid.uuid4())'; }
step() { printf '\n== %s\n' "$*"; }

step "1. Create repository $REPO"
curl -sSf -X POST "$BASE/repos" -H "$JSON" -d "{\"name\":\"$REPO\"}"; echo

step "2. Upload a geometry blob (opaque bytes; a real client sends openNURBS archive bytes)"
BLOB=$(mktemp)
printf 'pretend these are openNURBS archive bytes of a Brep' > "$BLOB"
GEO=$(sha "$BLOB")
curl -sSf -X POST "$BASE/repos/$REPO/objects/missing" -H "$JSON" -d "{\"hashes\":[\"$GEO\"]}"; echo
curl -sSf -X PUT "$BASE/repos/$REPO/objects/$GEO" -H 'content-type: application/octet-stream' --data-binary @"$BLOB"; echo

step "3. First commit: settings, two layers, one object on 'Walls'"
DEFAULT=$(guid); WALLS=$(guid); BOX=$(guid)
FIRST=$(curl -sSf -X POST "$BASE/repos/$REPO/commits" -H "$JSON" -d @- <<EOF | jget commit
{
  "branch": "main", "expectedHead": null, "message": "Initial document",
  "author": { "id": "alex", "name": "Alex", "kind": "human" },
  "changes": [
    { "op": "put", "kind": "settings", "id": "settings",
      "manifest": { "attributes": { "unitSystem": "Millimeters", "absoluteTolerance": 0.001 } } },
    { "op": "put", "kind": "layer", "id": "$DEFAULT", "manifest": { "attributes": { "name": "Default", "color": "#000000" } } },
    { "op": "put", "kind": "layer", "id": "$WALLS", "manifest": { "attributes": { "name": "Walls", "color": "#c0392b" } } },
    { "op": "put", "kind": "object", "id": "$BOX",
      "manifest": { "subtype": "Brep", "attributes": { "name": "Box" },
                    "components": { "geometry": "$GEO" },
                    "refs": [ { "kind": "layer", "id": "$WALLS", "role": "layer" } ] } }
  ]
}
EOF
)
echo "commit $FIRST"

step "4. List the snapshot with manifests"
curl -sSf "$BASE/repos/$REPO/commits/main/entities?manifests=true" | python3 -c 'import sys, json
for e in json.load(sys.stdin)["entities"]:
    m = e["manifest"]
    print("  {:8} {}  version {}  components={}  refs={}".format(e["kind"], e["id"], e["version"][7:19], sorted(m["components"]), len(m["refs"])))'

step "5. Branch agent/study and let an agent add a layer there"
curl -sSf -X PUT "$BASE/repos/$REPO/refs/branches/agent/study" -H "$JSON" -d "{\"commit\":\"$FIRST\",\"expected\":null}"; echo
curl -sSf -X POST "$BASE/repos/$REPO/commits" -H "$JSON" -d @- <<EOF | jget commit
{
  "branch": "agent/study", "expectedHead": "$FIRST", "message": "Add study layer",
  "author": { "id": "agent:study-bot", "kind": "agent" }, "meta": { "agent.model": "example" },
  "changes": [ { "op": "put", "kind": "layer", "id": "$(guid)", "manifest": { "attributes": { "name": "Study" } } } ]
}
EOF

step "6. Meanwhile a human renames the box on main"
HUMAN=$(curl -sSf -X POST "$BASE/repos/$REPO/commits" -H "$JSON" -d @- <<EOF | jget commit
{
  "branch": "main", "expectedHead": "$FIRST", "message": "Rename box",
  "author": { "id": "alex", "kind": "human" },
  "changes": [ { "op": "put", "kind": "object", "id": "$BOX",
    "manifest": { "subtype": "Brep", "attributes": { "name": "Box A" }, "components": { "geometry": "$GEO" },
                  "refs": [ { "kind": "layer", "id": "$WALLS", "role": "layer" } ] } } ]
}
EOF
)
echo "commit $HUMAN"

step "7. Merge agent/study into main"
curl -sSf -X POST "$BASE/repos/$REPO/merges" -H "$JSON" \
  -d "{\"branch\":\"main\",\"expectedHead\":\"$HUMAN\",\"theirs\":\"agent/study\",\"author\":{\"id\":\"alex\",\"kind\":\"human\"}}"; echo

step "8. Commit graph (newest first)"
curl -sSf "$BASE/repos/$REPO/graph?limit=20" | python3 -c 'import sys, json
for c in json.load(sys.stdin)["commits"]:
    print("  gen {}  {}  parents={}  {:5}  {}  {}".format(c["generation"], c["id"][7:19], len(c["parents"]), c["author"]["kind"], c["message"], c["refs"]))'

step "9. Diff from the first commit to main"
curl -sSf "$BASE/repos/$REPO/diff?from=$FIRST&to=main" | python3 -m json.tool

step "10. The server refuses an invalid snapshot: deleting a layer that still has objects"
HEAD=$(curl -sSf "$BASE/repos/$REPO/refs/branches/main" | jget commit)
curl -sS -X POST "$BASE/repos/$REPO/commits" -H "$JSON" \
  -d "{\"branch\":\"main\",\"expectedHead\":\"$HEAD\",\"message\":\"oops\",\"author\":{\"id\":\"alex\",\"kind\":\"human\"},\"changes\":[{\"op\":\"delete\",\"kind\":\"layer\",\"id\":\"$WALLS\"}]}" \
  -w '\nHTTP %{http_code}\n'

rm -f "$BLOB"
printf '\nDone. Repository %s is still on the server.\n' "$REPO"
