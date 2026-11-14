# Backend changes for the Rhino commands

The Rhino commands (`rhino/rhinotrunk.py`: COMMIT, PULL, BRANCH, MERGE, COMPARE) were built to exercise the engine. This page lists every change they required on the server, why each was needed, and what is still missing. The API reference is [API.md](API.md).

All changes are backwards compatible: existing requests behave as before, except the two new rules marked **(behaviour change)**.

## Summary

| # | Change | Needed by | Endpoint(s) |
| --- | --- | --- | --- |
| 1 | Branch metadata: parent branch, creator, base commit | BRANCH, MERGE, COMPARE | `GET/POST /branches`, `PUT refs/branches` (new fields), `POST /commits` (`parent`) |
| 2 | Repository policy: protected branches | COMMIT, MERGE | `GET/PUT /policy` |
| 3 | Agents cannot commit to, move or delete protected branches **(behaviour change)** | COMMIT | `POST /commits`, `PUT/DELETE refs/branches` → 403 `forbidden` |
| 4 | Human approvals for merges, enforced on every merge **(behaviour change for agents)** | MERGE, COMPARE (Approve) | `POST/GET /approvals`, `POST /merges` (`approval`) → 403 `approval_required`, 409 `approval_stale` |
| 5 | Merge rules: resolve conflicts automatically, but flag them for approval | MERGE | `PUT /policy` (`mergeRules`), `POST /merges` (`rules`, `approveRuleResolutions`) |
| 6 | Merge preview (dry run) | MERGE, COMPARE | `POST /merges/preview` |
| 7 | Diff with manifests in one call | PULL, COMPARE | `GET /diff?manifests=true` |
| 8 | Merge base endpoint | COMPARE, clients | `GET /merge-base` |
| 9 | Small mutable metadata store | 1, 2, 4 | storage (`IMetaStore`) |

Tests: `tests/RhinoTrunk.Tests/PolicyAndPreviewTests.cs` (9 new tests). All 50 tests pass. Two existing tests had agents committing straight to `main`; they now commit to agent branches, as change 3 requires.

## 1. Branch metadata

**Why.** MERGE "pulls the current branch back into its parent (main or otherwise)". Git has no notion of a parent branch, so the server must record it. MERGE also has to know who created a branch, because an agent may merge without approval only into a branch it created.

**What.**

- When a branch is created, the server records `parent`, `base` (the commit it started at), `createdBy` and `created`.
- Ways to create a branch:
  - `POST /repos/{repo}/branches { name, from, parent?, author }`.
    - `from` is any revision.
    - `parent` defaults to `from` when `from` names a branch, so branching off a branch works.
  - `PUT refs/branches/{name}` with new optional `author` and `parent` fields.
  - The first commit of a new branch, with a new optional `parent` field on `CommitRequest`.
- `GET /branches` and `GET /branches/{name}` return `BranchInfo`, which also includes the computed `protected` flag.
- Deleting a branch deletes its metadata.

**Limits.**

- Branches created before this change have no metadata. MERGE then defaults to `main`, and COMPARE lists them as open proposals anyway.
- Renaming a branch is not supported.

## 2–3. Protected branches

**Why.**

- "At this stage we will never allow an agent to merge back into main without explicit approval."
- If agents could still commit to main directly, or move the `main` ref, that rule would mean nothing.

**What.**

- `RepoPolicy { protectedBranches (default ["main"]), mergeRules }`, read with `GET /policy`.
- `PUT /policy` changes it, and only a human author may do so.
- For agents (`author.kind = agent`), protected branches block:
  - commits (403 `forbidden`);
  - moving the ref directly through `PUT refs/branches/{name}`, which now requires a human `author`.
- Nobody can delete a protected branch without changing the policy first.
- Humans may still commit to `main` directly. COMMIT does not forbid it, but the team may want to.

## 4. Approvals

**Why.**

- "Human approval is ALWAYS required for merging back into main."
- "An AI may merge into a branch that it created."
- The approval has to survive between the human's action in Rhino and the agent's merge, and has to be checkable by the server.

**What.**

- `POST /approvals { branch, theirs, by (human), note }` records an approval for exactly the current pair of heads (`ours`, `theirs`).
- Every merge works out why it needs approval (`approvalReasons`):

| Reason | When |
| --- | --- |
| `protected_branch` | the target is protected |
| `rules_applied` | merge rules resolved conflicts (change 5) |
| `not_branch_owner` | an agent merges into an unprotected branch it did not create |

- How the requirement is met:
  - **Human author:** the merge request itself is the approval (it is the merge confirmation in the UI). Rule resolutions also need `approveRuleResolutions: true`. The approver is recorded in the commit's `meta["merge.approvedBy"]` and in the response's `approvedBy`.
  - **Agent author:** the merge needs `approval: <id>` of an active human approval.
    - An approval is single-use and becomes `stale` as soon as either head moves (409 `approval_stale`). The human therefore approves exactly what they previewed.
    - Without an approval: 403 `approval_required`, with `data.reasons`.
- `GET /approvals?branch=&theirs=` lists approvals with their computed `status` (`active`, `used`, `stale`). An agent uses it to find out whether it may proceed.
- The Rhino client also posts a `review/merge-request` annotation on the agent's head. This gives a PR-like signal without a new resource.

**Limits.**

- Identity is self-declared (no auth yet, [OPEN_QUESTIONS.md](OPEN_QUESTIONS.md) #4), so these rules keep honest clients and agents in their lane but are not a security boundary.
- An approval does not record the resolutions or rules the human saw. Only the heads are bound.

## 5. Merge rules

**Why.** "If merges can be resolved through defined rules, then a merge can be made. This still requires human approval but should be indicated."

**What.**

- A rule is `{ name, kind?, fields?, take: ours|theirs }`.
- **Repository rules** live in the policy. **Per-request rules** come in the merge or preview request and are applied after the repository's rules. `useRepoRules: false` turns the repository's rules off.
- **Field-level conflicts** (both sides changed e.g. `components.attributes`):
  - A rule matches when it names the entity's kind (or none) and lists every conflicting field.
  - Only the conflicting fields take the rule's side; other fields still merge three-way.
- **Whole-entity conflicts** (added on both sides with different content, or modified on one side and deleted on the other): matched only by rules without `fields`.
- Explicit `resolutions` still beat rules.
- Every rule decision is returned as `autoResolved` (entity, fields, take, rule name) and adds `rules_applied` to the approval reasons. Merge commits record `merge.rules` and `merge.autoResolved` in `meta`.
- Implementation: `MergeManifests` gained a `prefer` argument; the merge loop moved into `PlanMergeAsync`, which merge and preview share.

## 6. Merge preview

**Why.** "In all instances of merging, we will need to load a preview of the merge into the display conduit." COMPARE also needs it: an option is a merge proposal into a given point on main, which may be older than the head.

**What.**

- `POST /merges/preview` runs the full merge plan and moves nothing.
- **Inputs:**
  - `branch`: the target, used for the approval rules.
  - `ours`: any revision; defaults to the branch head.
  - `theirs`, `resolutions`, `rules`, `author`.
  - `compareTo`: the commit the client's file is at.
  - `manifests`: whether to include manifests.
- **Outputs:**
  - `status`: `upToDate`, `fastForward`, `clean` or `conflicts`.
  - `tree`: the merged tree.
  - `canMerge`, `requiresApproval`, `approvalReasons`.
  - `conflicts`: with the `base`, `ours` and `theirs` versions, so a client can draw both sides in a diffing session.
  - `autoResolved`.
  - `validation`: the invariant errors a real merge would hit.
  - `incoming`: counts of what lands on `ours`.
  - `changes`: the diff from `compareTo` to the merged result, with manifests if requested.
- Cost: no lock is taken. It only writes content-addressed manifests and tree nodes, which a later real merge reuses. Previews took 2–40 ms in the tests below.

## 7. Diff with manifests

**Why.** "PULL … ENSURE THAT YOU ARE LOADING ONLY WHAT IS NEEDED. We are aiming for speed." Without manifests in the diff, PULL needs a diff call plus a batch call for the manifests before it knows which blobs to fetch.

**What.** `GET /diff?from=&to=&manifests=true` adds `fromManifest` and `toManifest` to each entry. PULL is now:

1. Get the diff from the file's commit to the target, with manifests.
2. Fetch only the attribute blobs for changed entities, plus geometry only for objects whose `geometry` component changed (or that were edited locally).

Attribute-only changes fetch no geometry.

## 8. Merge base

`GET /merge-base?a=&b=`. Clients previously walked `/graph` themselves. Cheap to expose, since the server already computes it for merges.

## 9. Metadata store

`IRepositoryStore.Meta`: a small key → JSON store, separate from content-addressed objects and refs.

| Key | Contents |
| --- | --- |
| `branch/<name>` | branch metadata |
| `policy` | repository policy |
| `approval/<id>` | one approval |

It has two implementations:

- **File system:** `meta/<url-escaped key>.json` in the repository directory.
- **In-memory:** a dictionary.

Branch metadata is written right after a successful ref compare-and-swap, so a crash between the two leaves a branch without metadata. That is harmless: it falls back to the defaults.

## Measured with the Rhino client (prototype data: pavilion-options, ~30–80 entities per state)

| Operation | Time | Notes |
| --- | --- | --- |
| PULL into an empty file | 330 ms | 4 HTTP calls, 27 geometry blobs, 137 KB |
| PULL between option branches | 130–460 ms | 4 calls; only differing entities written; time is mostly geometry transfer and Rhino object creation |
| PULL of an attribute-only change | 16–24 ms | no geometry fetched (2 KB) |
| PULL that only reverts local stray content | 34 ms | 3 calls, 0.7 KB |
| COMMIT (3 changes) | 40–60 ms | plus preview-mesh publishing |
| Merge preview | 20–100 ms | MERGE runs two previews (relative to the target and to the file) |
| COMPARE, 4–6 options | 30–60 ms previews, then switching 9–35 ms (cycle), 30–75 ms (overlay), 33–65 ms (side by side) | geometry cached by content hash |

## Not done / next

- **Auth.** Every rule above trusts `author.kind`. Needs real identities and a server-side notion of agent sessions.
- **A proposal (PR) resource.** COMPARE treats every unmerged unprotected branch as a proposal; the agent's "please merge" is an annotation. A first-class proposal (target, description, reviewers, status, discussion) would replace both.
- **Approval scope.** An approval binds the two heads only. It does not bind the resolutions or rules the human saw.
- **Annotation checks on protected branches** (e.g. require `ci/geometry-valid` success): [OPEN_QUESTIONS.md](OPEN_QUESTIONS.md) #5.
- **Attribute-level merge** (merging JSON fields inside `attributes`) would remove most rule use for attribute conflicts: [OPEN_QUESTIONS.md](OPEN_QUESTIONS.md) #15.
- **A combined preview** returning diffs against both the target and the client's commit would save MERGE one round trip.
- **Case-insensitive file systems.** File-system metadata keys use URL escaping, so branch names that differ only in case would share a file on Windows. Branch names are case-sensitive refs.
