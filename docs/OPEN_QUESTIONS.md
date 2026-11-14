# Open questions

Each question has the default the prototype uses, so work can continue until it is answered.

## Product and deployment

1. **Production storage.** This repository is named `git_engine` ("Back end git server"). Is git meant to be the production store, or one adapter among several? *Prototype:* file-system store behind `IRepositoryProvider`; git, SQLite and Postgres + object storage are adapters to add (review recommends a service-defined model with git as mirror).
2. **Deployment.** Central server only, or local-first repos that sync? *Prototype:* central server. Content-addressed ids keep local-first possible later.
3. **Multiple documents.** When do we need more than `doc = "main"`? Do linked blocks or worksessions map to documents? *Prototype:* the API carries `doc` everywhere; only `main` is used.
4. **Auth and identity.** Who may read and write which repos and branches? How are agents identified? *Prototype:* no auth; `author.id` and `author.kind` are self-declared.
5. **Branch protection.** Which branches require passing checks (annotations) before a merge? *Prototype:* none enforced.

## Data model

6. **Attribute schemas.** Who owns them (client team?), and are they versioned inside the JSON (e.g. `"schema": 1`)? *Prototype:* proposal in CLIENT_GUIDE.md section 3, unversioned.
7. **Session state vs versioned state.** Are hidden/locked flags, layer order and display order versioned? *Prototype:* versioned as attributes.
8. **Block members.** Separate kind, or `object` with an `owner` ref? *Prototype:* `object` with `owner` (not yet validated).
9. **Which references does the server enforce?** *Prototype:* existence of every declared ref; semantic rules only for `layer` (objects) and `parent` (layers). Material, linetype, group, block definition rules come next.
10. **Unique names.** Layers: unique among siblings, case-insensitive (to confirm with `ModelComponent.ModelComponentTypeIgnoresCase`). Block definitions, linetypes, dimension styles, groups: not yet enforced; derive the list from `ModelComponentTypeRequiresUniqueName`.
11. **Document minimums.** Exactly one settings entity and at least one layer per document — correct for every Rhino document we care about?
12. **Geometry encoding.** UTF-8 `ToJSON` text (prototype) or a binary envelope (smaller, needs its own version header)?
13. **Previews.** Rhino mesh JSON (`preview-mesh.v1`, prototype) or GLB for web viewers too? Who generates previews for agent commits without Rhino?
14. **Timestamps.** Server clock (prototype) or client clock?

## Merge and collaboration

15. **Attribute merges.** Merge attributes JSON field by field instead of conflicting on the whole component? *Prototype:* whole component.
16. **Conflict resolution beyond ours/theirs.** Allow a hand-edited manifest as resolution? *Prototype:* ours/theirs only, or commit a fix and merge again.
17. **Live collaboration.** Do we need advisory locks/claims on layers or objects to reduce conflicts?

## Scale and operations

18. **Large imports.** A 1M-object initial commit is one large request (hundreds of MB). Staged multi-request commits? *Prototype:* single request, 512 MB limit.
19. **Paging and transfer.** Entity lists and graph are unpaged; batch download is base64 JSON. Streaming packs?
20. **Delete validation.** Deleting entities scans the document for references (0.6 s at 100k objects). Add a reverse-reference index?
21. **Garbage collection and retention** of unreachable objects after failed commits or deleted branches.
22. **Concurrency.** The file-system store serializes writes inside one process; several server processes on one data directory are unsafe.

## Engineering

23. **.NET version.** .NET 8 matches Rhino 8 and `rhino_client`, but Microsoft's support for .NET 8 ended on 10 November 2026 ([announcement](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/)). Move the server and tests (not Contracts/Client, which must load in Rhino 8) to .NET 10 LTS?
24. **Test framework.** Tests use a small built-in runner because the build sandbox could not reach nuget.org. Switch to xUnit and add CI?
25. **Distribution of the SDK.** Project reference across sibling repositories (prototype), or a NuGet package (GitHub Packages)?
26. **Language of a future production server** if it outgrows .NET (Go/Rust for a git-backed store?). The HTTP contract stays the same.
