# Phase 1C.1 — durable single-file transfers

Implemented for review on 2026-10-25. Scope is one active file, durable history and
conservative explicit recovery. No live S3 access, infrastructure changes, commits
or pushes were performed. Queue scheduling/retries, multipart and partial resume
remain outside this increment. The existing create-only upload and native local
no-replace protections remain in force; provider conditional-write limitations
still apply.

## User-visible behavior

The collapsed **Transfer history and recovery** panel lists persisted results and
unresolved operations. Selection shows source/destination, endpoint/profile revision,
operation ID, observed bytes, last phase, status and available actions. History loads
without credentials or an S3 session. Nothing replays, reconciles or cleans up at startup.

- **Reconcile upload** explicitly resolves credentials for the unchanged profile
  revision/target and issues a full GET. Matching saved operation metadata, length
  and SHA-256 can establish Completed. A missing object, denied read, changed content,
  timeout or disconnect remains unresolved. The original local source need not still
  exist; verification uses its durable pre-PUT hash. No upload restart action exists.
  An unresolved matching upload target also blocks a fresh normal upload attempt.
- **Restart download** requires confirmation, a recorded temporary receipt, unchanged
  destination-parent identity and absence of the final filename. Explicit recovery
  removes only the verified owned temporary entry, then downloads the current remote
  object from byte zero under the same operation ID. It does not append or promise
  the remote object stayed unchanged between sessions. Existing final files remain
  untouched, including a file possibly published before the crash.
- **Dismiss resolved** removes only a history row. It never deletes user files or
  S3 objects. Unresolved outcomes or outstanding cleanup cannot be dismissed.

## Journal schema and ownership

`BucketBus/transfers/transfers.db` resides under the current user's
`Environment.SpecialFolder.LocalApplicationData`; it is separate from the executable
folder. Linux directory mode is 0700; database and owner lock are 0600. Windows uses
per-user application-data ACL inheritance, still requiring native qualification.
Known reparse paths are rejected. This is sensitive local metadata, not an encrypted
vault or a same-user security boundary.

Schema version 1 uses SQLite `user_version=1`, application ID `0x4242544a`, and:

```sql
CREATE TABLE transfers (
  id TEXT PRIMARY KEY NOT NULL,
  revision INTEGER NOT NULL,
  state INTEGER NOT NULL,
  payload TEXT NOT NULL
);
```

The typed JSON payload contains operation/profile IDs, profile revision and target
fingerprint, HTTPS authority, exact bucket/key/local path, direction, phase, state,
observed progress, prepared upload length/SHA-256, temporary ownership receipt,
cleanup/error categories and timestamps. No credentials, session tokens, signed
URLs, raw provider responses or credential objects are serialized. Identity/target
and established upload verification evidence cannot change across revisions.

All changes use short explicit transactions and revision checks. A lifetime
`owner.lock` excludes a second application journal owner; that instance can browse
but cannot transfer. SQLite calls run through a serialized worker off the UI thread
because the provider's asynchronous methods do not provide asynchronous I/O.
[Microsoft.Data.Sqlite async limitations](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async)

The database is created only when absent, with no orphan sidecars. Existing empty,
corrupt, foreign or future-version databases fail closed and remain preserved.
Initialization checks SQLite integrity and validates rows before recovery. Initial
schema creation and startup transitions are transactional; no existing-schema
migration or automatic repair is shipped. An operation/commit failure latches the
journal unavailable for that process. Preserve DB/WAL/SHM together with all instances
closed; do not delete them to unblock an uncertain write. Backup/export/repair UI is
not part of this increment.

## Durability and crash boundaries

WAL plus `synchronous=FULL` is set and checked. SQLite commits are durable subject to
filesystem/storage honoring synchronization; no transaction spans S3 or filesystem
work. This follows [SQLite's WAL/FULL contract](https://sqlite.org/pragma.html#pragma_synchronous).
The tests establish process-termination behavior, not resistance to defective disks,
network filesystems, kernel failures or arbitrary power loss. Keep the journal on
local storage. Local file publication and the journal are not one atomic transaction;
parent-directory power-loss durability has not been qualified.

| Last durable boundary | Startup interpretation / permitted action |
| --- | --- |
| Pending, before effects | Interrupted; no automatic execution |
| Upload Running with prepared hash/length, before PUT invocation | Outcome Unknown, conservatively even if the process died before sending |
| PUT sent, lost/acknowledged response, verification or final journal commit incomplete | Outcome Unknown; explicit positive full-content reconciliation only |
| Download Running, destination being created | Interrupted; a missing durable ownership receipt disables restart |
| Download Running, receipt recorded | Interrupted; confirmed restart checks destination and ownership before cleanup |
| Download Committing | Requires Reconciliation; a final file blocks restart and remains untouched |
| Terminal result committed | Completed, Failed or Cancelled history; cleanup obligations remain visible |
| Explicit recovery interrupted | Requires Reconciliation; never automatic retry |

Intent is committed before a PUT or destination creation. Hash/length and operation
identity are committed before upload. Download commit intent is saved before publishing
the final filename. Terminal state is saved only after verification, commit and cleanup
attempts; failure to record success is reported as unresolved, not Completed. Progress
is checkpointed at most once per second plus completion observations; it measures
observed bytes, not remote acceptance or guaranteed recoverable bytes on disk.

Linux receipts contain parent/container/payload device, inode and birth time; missing
birth-time support disables restart. Windows receipts use volume/file ID and creation
time. Recovery rejects links/reparse changes and substituted identities. The small
creation-to-receipt persistence gap deliberately blocks restart rather than scanning
or deleting unknown files. A download whose final file may already have been published
stays unresolved; automatic final-file adoption is not implemented. Pending uploads,
changed profiles and cleanup-only unresolved cases may require manual investigation;
confirmation is never a substitute for evidence.

## Executed validation

Fedora 44 x64; .NET SDK 10.0.113. Application tests used disposable local fixtures,
fake credentials and synthetic HTTP transports only.

| Check | Result |
| --- | --- |
| Main solution locked restore / Release | Passed, zero warnings/errors |
| Ordinary offline suite | 328 passed: Core 50, Platform 102, S3 92, Desktop 84; one opt-in native vault test skipped |
| Integration solution locked restore / Release | Passed, zero warnings/errors |
| Integration guards, `Category!=LiveS3` | 30 passed; no live tests |
| NuGet audit, both solutions, all transitive packages | No known vulnerabilities reported by the feed at this check |
| Linux x64 locked self-contained publish | Passed; published SQLite 3.53.3 loaded and executed a WAL/FULL transaction |
| Windows x64 locked self-contained publish / ZIP | Passed; x64 `e_sqlite3.dll` inspected and runtime notices included |
| Windows native journal/ACL/filesystem/GUI/vault execution | BLOCKED: Windows 11 VM unavailable |
| Actual Fedora GUI acceptance of new recovery panel | BLOCKED in this environment: no display session; headless tests passed |
| Power interruption, storage fault hardware, live WAN recovery | Not performed |

Subprocess fixtures terminate the process during an uncommitted journal update,
a staged download and after final local publication. Synthetic SDK subprocesses
terminate immediately before PUT invocation, after the transport receives PUT, and
after a lost-response/absent-read outcome. Reopening retains intent, rolls back
uncommitted progress, performs no remote I/O and never replays PUT. Other regressions
cover journal failures before effects/after apparent success, corrupt/future schemas,
concurrent owners, exact keys, changed profile/destination/temporary identities,
source-independent reconciliation, denial/404/mismatched identity/content, dismissal
restrictions and credential-free history UI. Existing Phase 1B.2 regressions remain.

New exact dependency: Microsoft.Data.Sqlite 10.0.13 (Core 10.0.13, MIT), bringing
SQLitePCLRaw bundle/core/provider/native packages 2.1.13 (Apache-2.0 packaging/bindings;
SQLite engine public domain). No ORM. All ordinary/integration/RID lockfiles are
updated. See [dependency inventory](DEPENDENCIES.md). The Windows packager's previous
references to absent public portable/validation documents were repaired with concise
public checklists; no private laboratory documents enter the ZIP.

## Manual acceptance checklist

Use disposable data and separately authorized provider access; these steps do not
extend live-test authorization.

1. Launch the packaged application, expand history and confirm it loads offline.
   Complete one upload and download, close/reopen and inspect their persisted records.
2. Interrupt a download by terminating the process. Reopen offline: expect Interrupted,
   no GET and no cleanup. Inspect the recorded target, confirm restart and verify the
   full resulting file. Create a conflicting final file first: restart must refuse it.
3. Interrupt an upload. Reopen: expect Outcome Unknown and no PUT. Explicitly reconcile:
   only matching operation identity and complete content may resolve it. A missing or
   inaccessible object must remain unresolved; no upload-restart button is available.
4. Change the saved profile or replace an owned temporary entry before recovery:
   expect refusal and preservation. Dismiss completed history and verify actual data
   remains. Unresolved records must not be dismissible.
5. With a disposable application-data test account, check a second instance and a
   corrupt/future-schema journal: transfers must be blocked without deleting evidence.
   Do not corrupt an existing user's journal. Run the [Windows native checklist](WINDOWS_VALIDATION.md)
   when the VM becomes available.

Stop at Phase 1C.1. Phase 1C.2 needs separate authorization.
