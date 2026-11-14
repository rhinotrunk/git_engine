# git_engine

Back-end versioning service for RhinoTrunk: git-like history for Rhino documents, for humans and agents. The service has no dependency on Rhino libraries; the Rhino 8 plugin (`rhino_client`) and agents talk to it over HTTP.

**Status:** prototype v0.1 — running and tested REST API, C# SDK, in-memory and file-system storage. See [docs/PROTOTYPE_PLAN.md](docs/PROTOTYPE_PLAN.md).

## Quick start

Requires a .NET SDK 8.0 or newer (the .NET 10 SDK works too) and the .NET 8 runtime, which `rhino_client` needs anyway.

```sh
dotnet build
dotnet run --project tests/RhinoTrunk.Tests     # 41 tests; exit code 1 on failure
dotnet run --project src/RhinoTrunk.Server      # http://localhost:5280 ; GET / lists all routes
./samples/demo.sh                               # curl walkthrough, in a second terminal
```

Storage defaults to the file system (`src/RhinoTrunk.Server/data/` when started with `dotnet run`). Use `Storage__Provider=InMemory` for a throwaway server, or `Storage__Root=/path` to choose the directory.

## How it works

- Every layer, object, material, … is an **entity** keyed by its Rhino GUID.
- Each entity version is an immutable **manifest** (named component blobs + references), addressed by its SHA-256 hash.
- A **commit** points to a Merkle tree (document → kind → 256 shards → entries), so commits and diffs cost time proportional to the change.
- The server validates every commit and merge (references, layer tree, unique names, settings) and moves branches only by compare-and-swap, so clients cannot create an invalid snapshot.

## Repository layout

```text
src/RhinoTrunk.Contracts   DTOs, canonical JSON, content hashes, naming rules (net8.0, no dependencies)
src/RhinoTrunk.Client      typed HTTP client for the Rhino plugin, CLI and tests (net8.0, no dependencies)
src/RhinoTrunk.Core        engine: storage abstraction + implementations, trees, validation, commits, merge, graph
src/RhinoTrunk.Server      ASP.NET Core minimal API
tests/RhinoTrunk.Tests     unit, engine and end-to-end HTTP tests (self-contained runner)
samples/demo.sh            the API with curl
docs/                      plan, API reference, client guide, open questions
```

## Documentation

- [docs/PROTOTYPE_PLAN.md](docs/PROTOTYPE_PLAN.md) — steps taken, hand-off milestones for the Rhino client, next server work
- [docs/API.md](docs/API.md) — endpoints, payloads, errors, invariants
- [docs/CLIENT_GUIDE.md](docs/CLIENT_GUIDE.md) — mapping a RhinoDoc to entities, serialization, status/commit/checkout flows
- [docs/OPEN_QUESTIONS.md](docs/OPEN_QUESTIONS.md) — decisions to make later, with the prototype's defaults
