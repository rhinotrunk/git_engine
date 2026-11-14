# Steps to a prototype

Goal: a running, tested versioning service that another party can build the Rhino 8 client against right away. Steps 1–4 are done in this repository; step 5 is the hand-off; steps 6–7 run in parallel with the client work.

Design background: [RhinoTrunk — Data Model & Storage Review](https://claude.ai/artifact/1DsPTWokzrzv9iV1i3cr7n).

## Step 1 — Freeze the prototype decisions (done)

| Decision | Prototype choice | Why |
| --- | --- | --- |
| Interface | REST API (+ C# SDK); CLI later | The Rhino client needs HTTP; agents can use curl or the SDK. |
| Stack | .NET 8, ASP.NET Core minimal API, zero NuGet dependencies | Same SDK as `rhino_client`; Contracts and SDK load inside Rhino 8 without assembly conflicts. |
| Model | Entities keyed by Rhino GUID; content-addressed manifests with named components; Merkle tree root → doc → kind → 256 shards; commits with generation numbers | As in the review: cost follows the change, identical content dedupes, diffs skip equal subtrees. |
| Storage | `IRepositoryProvider` with **in-memory** and **file-system** implementations | Simple, inspectable, swappable. SQLite/Postgres/git adapters plug in behind the same interfaces. |
| Constraints | Server-side Tier-1 invariants on every commit and merge; refs move only by compare-and-swap | The client cannot write an invalid snapshot. |
| Scope cut | No auth, no paging, no streaming packs, no field-level JSON merge, single server process | See [OPEN_QUESTIONS.md](OPEN_QUESTIONS.md). |

## Step 2 — Contracts and SDK (done)

- `src/RhinoTrunk.Contracts`: DTOs, entity kinds, error codes, naming rules, SHA-256 content hashes, RFC 8785-style canonical JSON.
- `src/RhinoTrunk.Client`: `RhinoTrunkClient` covering every endpoint, plus `Changes` helpers.

## Step 3 — Engine and storage (done)

- `TreeStore`: history-independent Merkle tree; apply, get, list, diff.
- `SnapshotValidator`: manifest integrity, reference integrity, dangling references, settings singleton, at least one layer, unique sibling layer names, no layer cycles, one layer per object.
- `RhinoTrunkService`: repos, objects, refs (CAS + reflog), commits, entity listing, diff with component detail, graph, entity history, three-way merge (entity → manifest field), annotations, derived data.
- Storage: `InMemoryRepositoryProvider`, `FileSystemRepositoryProvider` (`data/<repo>/objects/ab/…`, `refs.json`, `reflog.jsonl`).

## Step 4 — REST API, tests, demo (done)

- `src/RhinoTrunk.Server`: 28 routes; `GET /` lists them; errors use one JSON shape. Reference: [API.md](API.md).
- `tests/RhinoTrunk.Tests`: **41 tests, all passing** — canonical JSON, hashing, tree structure, every invariant, CAS conflicts, merges (fast-forward, clean, component-level, conflict + resolution, semantic conflicts), graph order, history, refs, annotations, derived data, file-system restart, and full end-to-end runs over HTTP with both storage providers.
- `samples/demo.sh`: the same workflow with curl against a running server, ending with a rejected invalid commit.

Measured on the prototype (in-memory storage, Release build, cloud VM, no HTTP):

| Operation | Time |
| --- | --- |
| Initial commit of 100,000 objects | 4.9 s |
| Commit editing 100 of them | 0.11 s |
| Diff of that commit (100 entries) | 0.02 s |
| Commit deleting 1 object (full dangling-reference scan) | 0.62 s |
| List 100,001 entities | 0.09 s |

### Verify it yourself

```sh
cd git_engine
dotnet build
dotnet run --project tests/RhinoTrunk.Tests          # prints PASS/FAIL per test, exit code 1 on failure
dotnet run --project src/RhinoTrunk.Server           # http://localhost:5280, data in src/RhinoTrunk.Server/data
./samples/demo.sh                                    # in a second terminal
```

## Step 5 — Hand off to the Rhino client team (next)

Give them this repository, [CLIENT_GUIDE.md](CLIENT_GUIDE.md) and [API.md](API.md). Suggested milestones, each demoable on its own:

| # | Milestone | Done when |
| --- | --- | --- |
| C1 | Connect | Plugin references `RhinoTrunk.Client`, lists repos and branches in the panel. |
| C2 | Import | "Share document" creates a repo and commits settings, layers and objects (geometry + attributes + refs). |
| C3 | Checkout | A commit opens into an empty document with the same ids, layers, colors and geometry. Round-trip C2 → C3 → status shows no changes. |
| C4 | Status and commit | Runtime-serial dirty tracking; commit only changed entities; handles 409 and 422 in the UI. |
| C5 | Graph and diff | Panel shows the commit graph (lanes by generation, human vs agent); selecting two commits highlights changed objects. |
| C6 | Branch and merge | Create a branch, switch, merge with conflict picker (ours/theirs). |
| C7 | Previews | Upload `preview-mesh.v1` on commit; view any commit from previews without checkout. |

## Step 6 — Server work in parallel (next)

| # | Task | Unblocks |
| --- | --- | --- |
| S1 | Decide production storage and deployment ([OPEN_QUESTIONS.md](OPEN_QUESTIONS.md) #1–2); add the chosen adapter (SQLite, Postgres + S3, or git) | Multi-user pilot |
| S2 | Auth: per-user and per-agent tokens; author taken from the token | Pilot with agents |
| S3 | Paging for entities and graph; streaming pack download instead of base64 batch | Documents beyond ~100k objects |
| S4 | Reverse-reference index so deletes skip the full scan; incremental validation | 1M-object documents |
| S5 | Branch protection: required annotations (CI checks) before a merge into `main` | Safe agent workflows |
| S6 | CLI (`rtk log`, `rtk diff`, `rtk commit-json`) on the SDK; MCP server for agents | Agents without the SDK |
| S7 | Move tests to xUnit and add CI (GitHub Actions) | Team workflow |

## Step 7 — Spikes to schedule

From the review: serialization stability across Rhino service releases; runtime-serial dirty tracking; preview packs at 1M objects; HAMT vs fixed sharding at 1M; render-content XML round-trip; checkout into a live document with id collisions.
