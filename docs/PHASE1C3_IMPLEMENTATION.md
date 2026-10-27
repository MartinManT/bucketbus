# Phase 1C.3 implementation evidence

2026-10-27 · Implemented for review; pre-alpha. No live S3 access, staging, commit,
push or infrastructure changes. Earlier uncommitted 1C.2a/1C.2b work is preserved.

## Behavior and boundaries

- Below **128 MiB**, including zero bytes: existing conditional single PUT.
- At/above **128 MiB**: sequential multipart, initially **16 MiB** per part, growing
  in whole MiB to fit **10,000 parts**. Minimum 5 MiB except the final part; maximum
  5 GiB per part. Checked 64-bit arithmetic; initial application ceiling **5 TiB**.
  Smaller provider limits can reject requests. This conservative ceiling does not
  claim every provider accepts that size. [AWS part limits](https://docs.aws.amazon.com/AmazonS3/latest/userguide/qfacts.html)
- Each part is a seekable bounded view of the pinned source. Payloads are streamed,
  not retained in RAM or copied to temporary source files. Control responses are
  bounded to 2 MiB; paginated ListParts accepts at most 1,000 rows per page and
  requires monotonic exact part agreement. Memory for receipts scales to 10,000 parts.
- Queue preparation hashes the source; execution/recovery reopens, hashes and compares
  its exact native identity/contents. Identity checks surround parts; full source
  validation precedes completion. Part hashing and SDK signing add bounded reads;
  full remote read-back verifies original SHA-256, length and operation metadata.
  These intentional I/O costs remain. Per-part identity checks avoid repeatedly
  hashing the whole file before every part. Shutdown cancels source preparation.
- Completion always uses `If-None-Match: *`; 412/409 conflicts stop without recreation
  or unconditional fallback. One-send transport guards and zero SDK retries also
  cover initiation, parts and completion. Real SDK fixture tests verify embedded
  HTTP-200 errors are not reported as success and do not produce another send.
  [SDK conditional completion contract](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/S3/TCompleteMultipartUploadRequest.html)
- Acknowledged-part bytes drive progress. The UI stays below 100% until completion
  and verification succeed. Browsing, preferences, the sequential queue, pause/cancel
  and bounded download retries are preserved. No multipart ETag-as-MD5 assumption.

## Journal and explicit recovery

SQLite remains **schema 2**: an optional multipart JSON payload stores the plan,
upload ID, ordered confirmed part numbers/sizes/ETags/SHA-256, pending part and phase.
No DDL migration or package changes. Legacy payloads load with null multipart state;
existing migration rollback/crash tests still pass. WAL/FULL, exclusive ownership,
short transactions and revision checks remain. Binary downgrade with newer records
is unsupported. No credentials, session tokens or signed request URLs are stored.

Durable barriers precede initiation, part sends, completion and explicit abort;
acknowledgements are persisted before the next operation. Failure to persist an
acknowledged ID leaves an ownership gap, not a claim that no upload exists.

Startup remains offline and paused. Incomplete multipart records become Interrupted;
completion intent/acknowledgement remains Outcome Unknown. A durably confirmed abort
can finish its terminal state without network access. Confirmed user actions offer:

- **Resume multipart upload:** original profile revision/endpoint/bucket/key, exact
  source identity and full hash must match; paginated ListParts must agree with the
  durable receipt ledger before remaining parts are sent. No new upload session.
- **Abort incomplete upload:** only the exact durably owned ID, with no uncertain
  completion. Abort acknowledgement (or explicit NoSuchUpload) is followed by a
  ListParts absence check. Retained/uncertain parts keep the cleanup obligation.
  This action never calls DeleteObject.
- **Reconcile upload:** uncertain completion requires matching operation metadata,
  total length and full remote SHA-256. Absence/denial/mismatch does not prove failure.

A lost part acknowledgement disables resume; this increment does not adopt or resend
that part. A lost upload ID cannot be safely aborted by BucketBus. Unknown completion
cannot be replayed or aborted. Provider storage may remain until explicit safe cleanup
or provider lifecycle handling; BucketBus never changes lifecycle policy.

## Executed validation

Fedora 44 x64 · .NET SDK 10.0.113. Existing exact dependency versions/lockfiles unchanged.

| Check | Result |
| --- | --- |
| Locked restore and Release solution build | Passed, 0 warnings/errors |
| Core offline tests | 81 passed |
| Platform offline tests | 126 passed; 1 opted-in native vault test skipped |
| S3 offline SDK tests | 149 passed |
| Desktop/headless tests | 186 passed |
| Total ordinary tests | **542 passed, 1 skipped**; 79 added cases |
| Separate integration guard build/tests | **40 passed**; all LiveS3 tests excluded |
| Main/integration transitive NuGet audits | No known vulnerable packages reported |
| Linux self-contained publish | Passed; published SQLite 3.53.3 WAL/FULL transaction passed |
| Windows x64 ZIP / native asset checks | Passed on Fedora; native Windows execution BLOCKED |

Coverage includes logical >5 GiB offsets/selection and 5 TiB planning without physical
multi-GiB fixtures, bounded source reads, exact Unicode keys, short parts/source changes,
lost creation/part/completion responses, cancellation before/during completion, full
read-back mismatch, conditional rejection, part disagreement/pagination, exact abort,
profile/source/credential gates and headless recovery actions/shutdown.

Ten new subprocess termination cases exercise lifecycle boundaries and kills inside
real SQLite save transactions. They preserve pre-commit evidence, waiting jobs and
no-network startup. They do not establish hardware power-loss durability.

The separately opted-in multipart qualification harness is implemented but **NOT RUN**.
Earlier Ceph Squid 19.2.7 single-PUT evidence is not multipart qualification. Fresh
endpoint and isolated write-scope approval is required; see [live instructions](LIVE_QUALIFICATION.md#multipart-qualification).
No public Ceph/AWS multipart compatibility claim is made. Conditional-header-ignoring
providers can overwrite; success/read-back cannot prove protection of a previous object.

## Manual GUI checks (not executed here)

Use an explicitly authorized disposable provider scope with normal HTTPS validation.
No qualification JSON installation is required for normal desktop uploads.

1. Fedora: run `artifacts/session5/linux-x64/BucketBus.Desktop` from an interactive
   desktop. Windows 11: extract `artifacts/session6/BucketBus-win-x64.zip` and run
   `BucketBus/BucketBus.Desktop.exe` as a normal user. Check startup, vault behavior,
   browse/navigation and saved display preferences. This workspace has no graphical
   session and no Windows VM, so neither platform's new GUI acceptance is claimed.
2. Queue an empty/small file and a disposable file at least 128 MiB; on a suitable
   test connection also use an existing >5 GiB fixture. Confirm sequential execution,
   acknowledged part progress, Completing/Verifying stages and final SHA-256 evidence.
   Download to a new directory; compare with `sha256sum` on Fedora or
   `Get-FileHash -Algorithm SHA256` on Windows.
3. Queue the same destination again. Expect Conflict and unchanged original content
   on a qualified provider. Run the separately approved harness for the concurrent
   completion race; UI success alone cannot establish provider conditional semantics.
4. Browse and change both display preferences while uploading. Pause after current;
   cancel during a part and inspect the unresolved record. No next queued job starts.
   Close during source preparation: shutdown should cancel that scan promptly.
5. Restart: no credentials prompt, network, automatic resume or cleanup. For an
   interrupted job captured between acknowledged parts, confirm Resume and verify
   remaining parts/complete read-back. A changed source/profile or mismatching remote
   ledger must block progress. An unacknowledged part must leave Resume unavailable.
6. Confirm Abort for an owned incomplete upload and inspect cleanup confirmation.
   Unknown completion must expose reconciliation instead of Resume/Abort; denied or
   missing read-back must remain unresolved. Verify unrelated objects remain untouched.

Known limits: sequential parts only; no automatic upload retries/resume; ambiguous
part adoption is unavailable; provider-specific maximums/conditional behavior and
actual multi-GiB WAN runs remain unqualified. The existing two-hour session deadline
still applies, so very slow/large work can stop before completion. Full hashing and
verification cost I/O; the bounded receipt ledger is rewritten in JSON at each
barrier. Native Windows, interactive GUI, live Ceph/AWS and hardware power-loss gates
remain open. No later increment starts automatically.

## Changed files in this increment

- `AGENTS.md`
- `README.md`
- `docs/ARCHITECTURE.md`
- `docs/CEPH_COMPATIBILITY.md`
- `docs/DECISIONS.md`
- `docs/LIVE_QUALIFICATION.md`
- `docs/PHASE1C3_IMPLEMENTATION.md`
- `docs/ROADMAP.md`
- `docs/SECURITY_MODEL.md`
- `docs/TEST_STRATEGY.md`
- `src/BucketBus.Core/MultipartUpload.cs`
- `src/BucketBus.Core/TransferCoordinator.cs`
- `src/BucketBus.Core/TransferJournal.cs`
- `src/BucketBus.Core/Transfers.cs`
- `src/BucketBus.Desktop/MainWindow.axaml`
- `src/BucketBus.Desktop/ViewModels/TransferViewModel.cs`
- `src/BucketBus.Platform/SqliteTransferJournal.cs`
- `src/BucketBus.Platform/TransferFiles.cs`
- `src/BucketBus.S3/ExactKeyS3Client.cs`
- `src/BucketBus.S3/PartStream.cs`
- `src/BucketBus.S3/S3MultipartSession.cs`
- `src/BucketBus.S3/S3TransferFactory.cs`
- `src/BucketBus.S3/S3TransferSession.cs`
- `tests/BucketBus.Core.Tests/MultipartPlanTests.cs`
- `tests/BucketBus.Desktop.Tests/MultipartViewTests.cs`
- `tests/BucketBus.Desktop.Tests/MultipartWorkflowTests.cs`
- `tests/BucketBus.Desktop.Tests/TransferWorkflowTests.cs`
- `tests/BucketBus.Platform.Tests/MultipartJournalTests.cs`
- `tests/BucketBus.S3.IntegrationTests/MultipartGuardTests.cs`
- `tests/BucketBus.S3.IntegrationTests/MultipartQualificationTests.cs`
- `tests/BucketBus.S3.Tests/MultipartTests.cs`
