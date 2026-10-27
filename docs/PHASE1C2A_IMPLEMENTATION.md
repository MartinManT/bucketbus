# Phase 1C.2a — persistent sequential transfer queue

Implemented for review on 2026-10-26. No live S3 access, infrastructure changes,
commits or pushes. Four production modules and one process are retained. Phase
1C.2b retries and all later work remain separately gated.

## Behavior

Select regular files locally or actual object rows on the current remote page,
then Add selected uploads/downloads (Ctrl+U/Ctrl+D). Start / Continue explicitly
runs waiting jobs in FIFO order, one transfer at a time. More selections can be
added while a transfer runs. Directories, links, common prefixes and other pages
are never recursively queued. Mixed unsupported selections disable the action.

Pause after current stops scheduling without cancelling the active transfer.
Cancel active requests cancellation and stops scheduling; it preserves uncertain
PUT outcomes. Any failed, cancelled, uncertain or cleanup-required result stops
the loop and leaves waiting jobs intact. Continue selects only Queued rows; it
does not retry failed/interrupted work. Recovery/history remains available through
the compact expandable transfer panel, alongside removal of never-started jobs.
Rows show filename/direction/state, queue position and individual byte progress;
exact source/destination, profile binding, error and recovery details are expandable.
Overall progress is a completed-this-run count plus waiting/active counts, avoiding
misleading byte percentages when download sizes are unknown or changing.

Queued intent captures exact keys/paths and immutable profile revision/authority.
Upload enqueue reads SHA-256, length and native file identity before persistence;
execution rereads/revalidates them before PUT. Missing, changed or replaced files
fail safely. Downloads revalidate the local destination through the existing
native no-replace creation/commit. Listed download sizes are display hints until
the actual GET supplies authoritative length. No credentials/client/network are
needed to enqueue. Execution resolves explicit credentials afresh.

Startup loads history and queue offline and never starts processing. Queued means
never claimed. Claimed Pending jobs recover as Interrupted; active uploads recover
as Outcome Unknown, with existing download commit/reconciliation rules unchanged.
Unknown writes are never replayed. Target protection compares authority/bucket/key
across profiles and signing-region changes; no unconditional PUT fallback exists.

## Journal schema 2 and migration

The same per-user database, lifetime owner lock, WAL/FULL policy and serialized
worker remain. No second persistence store or new dependency was introduced.

Schema 2 adds nullable unique `sequence INTEGER` to `transfers`. `QueueOrder` in
the typed JSON payload agrees with that column; optional `SourceIdentity` records
the native upload fingerprint. Existing enum values retain their numbers and
Queued is appended. Operation IDs, existing revisions, bindings, hashes, receipts,
cleanup flags and history are preserved.

Existing schema-1 rows are validated before DDL. One transaction adds the column,
creates its unique index and advances `user_version` to 2. Legacy rows retain null
sequence and are never converted to Queued. Failure/termination before commit
rolls back the entire migration; unsupported schemas/corruption remain preserved
and block transfer writes. No reset, delete, repair or automatic backup is performed.

Enqueue allocates FIFO sequence and inserts Queued in one transaction. Claiming
Queued to Pending and removing a Queued row each use their own atomic transaction;
a concurrent removal/claim can succeed only once. Removing started work returns
false without poisoning the journal. Every queued operation retains its original
ID through execution and recovery. No database transaction spans network or file
transfer work. Existing process-crash versus power-loss limitations still apply.

## Executed validation

Fedora development environment, .NET SDK 10.0.113, disposable local fixtures and
synthetic transports/credentials only. No package/version/lockfile changes.

| Check | Result |
| --- | --- |
| Main locked restore / Release build | Passed, zero warnings/errors |
| Offline suite | 393 passed: Core 63, Platform 119, S3 92, Desktop 119; one native vault test skipped |
| Added tests | 28 queue, UI, migration and crash cases; existing safety regressions retained |
| Integration locked restore / Release / guards | Passed; 30 guards, no live tests |
| NuGet audits, both solutions including transitives | No known vulnerabilities reported by the feed at this check |
| Native Fedora GUI | Not executed: no graphical session in this environment |
| Native Windows 11 | BLOCKED: VM unavailable |

Tests exercise FIFO/single worker, enqueue during activity, pause without cancelling,
cancellation uncertainty, middle conflict/permission/network failures, explicit
continuation without retry, changed/replaced/missing sources, destination conflicts,
profile/endpoint/bucket edits, protected unresolved targets across profiles, atomic
claim/removal and concurrent instance exclusion. A synthetic SDK subprocess killed
after PUT preserves active uncertainty plus remaining Queued rows. Legacy fixtures
preserve history/safety data through migration; exception and actual process-kill
faults during DDL prove rollback. The headless multi-selection test covers five
uploads, two exact remote keys, display changes during execution, pause, removal,
explicit continuation and native local download commits.

## Limits and manual smoke test

- Enqueue hashes complete upload sources, so adding large files requires local I/O.
  Preparation is off the UI thread; batches may be partially added if a later item
  cannot be prepared/persisted. The UI reports the added count and stops adding.
- Duplicate unresolved destination targets are refused. Downloads with identical
  final leaf names need separate destination directories; no overwrite choice exists.
- Changed profiles or failed jobs require an explicit fresh enqueue under the desired
  binding; Continue advances only remaining Queued work. Interrupted/unknown cases
  keep Phase 1C.1's conservative limits, including ownership/commit gaps.
- No retries, recursive transfers, parallel workers, multipart or partial resume.
  Native Windows journal/file behavior and actual desktop accessibility remain open.

On Fedora, and later on the authorized Windows VM, use disposable local data and
separately authorized provider access (these steps grant no new live authority):

1. Select five files, including Unicode/zero-byte fixtures, and Add selected uploads.
   Confirm nothing starts until Start / Continue; watch one active file at a time.
2. Add more selected files/objects while active. Change display preferences and
   browse elsewhere; verify captured targets and FIFO order remain unchanged.
3. Pause after current, close/reopen and confirm waiting jobs remain Queued without
   network activity. Remove one waiting job, then explicitly Continue.
4. Trigger a destination collision or change/delete an upload source after enqueue.
   Confirm the queue stops, preserves existing data and leaves later jobs queued.
5. Kill a disposable test process during upload; reopen and inspect Outcome Unknown
   plus waiting jobs. Continue must never replay that uncertain PUT. Use existing
   explicit reconciliation only. Verify native Windows behavior when the VM exists.

Stop after Phase 1C.2a for review.
