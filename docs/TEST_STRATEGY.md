# BucketBus test and release strategy

Status: DRAFT validation plan · 2026-10-21

**Sessions 1–4 pass 168 offline tests, with one native-vault test skipped**; [Session 4 results and limits](SESSION4_IMPLEMENTATION.md) distinguish this from native desktop and live provider qualification, which remain pending. The broader suites below remain planned validation. Local connectivity records do not establish application compatibility. Documentation research describes interfaces; support claims require recorded application tests against identified builds and configurations.

The [engineering review and Phase 1A plan](PHASE1A_PLAN.md) supplies concrete offline/live test tasks, package candidates, failure paths and platform exit gates. Phase 1A now includes read-only browsing; transfer writes/recovery remain 1B/1C. Compilation and offline application checks are recorded in the session evidence; native integration and live provider qualification remain unverified.

## Evidence levels and environments

| Layer | Environment | Proves | Does not prove |
| --- | --- | --- | --- |
| Pure behavior tests | Linux/Windows CI | State transitions, mapping, conflict policy, bounded scheduling, parsing/redaction | S3 wire compatibility or real OS UI behavior |
| HTTP fixtures/fault server | Local CI | Response handling, malformed payloads, disconnect timing, retry budgets | Provider authorization or consistency |
| Container S3 mock | Docker on Fedora/CI; Podman feasibility later | Supported object/multipart flows with selected SDK version | AWS parity, Ceph admin, true permissions, all addressing styles |
| Real Ceph RGW | Identified Squid build/configuration; explicitly authorized isolated test scope | Only tested S3/auth/config behavior; later Admin Ops/IAM need separate qualification | Every Ceph deployment, untested permissions, or later API families |
| AWS S3 | Dedicated nonproduction account and bounded test buckets | AWS S3 general-purpose bucket behavior and policy enforcement | Ceph compatibility or account-wide enterprise controls |
| Desktop integration | Windows 11 VM and named Linux desktops | Vault integration, keyboard, drag/drop, DPI, packaging, filesystem behavior | All desktop/session/network/filesystem combinations |

Planning inputs: typical files under 1 GB, both LAN and WAN/VPN target networks, Squid (19.x) as the primary initial Ceph validation family, and Fedora/.NET 10 SDK/Docker as the development baseline. Typical size is not a maximum supported file-size limit. Package/build provenance, backports, configuration, permissions and test authority must be recorded for each qualification run. Windows 11 VM checks remain a Phase 1A prerequisite; AWS test access/budget and signing arrangements must also be resolved. Enterprise proxy use and TLS interception are not assumed; track them in Q09. Local provisioning details, credential-source identifiers and raw run evidence stay outside public documentation.

ADR-006 is approved scope, not test evidence: persist queued/interrupted jobs, uncertain outcomes and known cleanup obligations; bound retries; retry multipart parts safely in-session; reconcile before confirmed whole-object restart after application restart. Cross-restart partial-byte resume is deferred to Phase 2. SQLite remains preferred and conditionally recommended by engineering review; selection approval and implementation validation remain open. Maximum file size, object counts and interruption frequency remain unmeasured (Q02).

## Live integration access and safety

Keep live RGW tests separate from unit tests. Require explicit opt-in and explicit endpoint, signing region, addressing mode, test bucket/scope and credential-source selection. A configured profile or reachable network never opts in. Evaluate the guard before credential resolution, client construction or network access. Default local and pull-request runs must not contact live infrastructure. Opt-in absent means skipped with no external I/O; explicitly opted-in runs with missing/invalid configuration or access fail or are recorded as blocked, never pass. Trusted scheduled jobs need separate authorization and an isolated target; shared manual-testing data is not an unattended CI fixture.

Begin an authorized target assessment with non-destructive connectivity, bucket listing where allowed, direct-bucket access, bounded object listing and metadata reads. A single listing page cannot validate pagination; reads with one identity cannot establish denied/restricted permissions. Use AWS SDK for .NET v4 with explicit custom-endpoint and path-style support when implementing backend tests. Resolve only the selected credential source; never hardcode, print, commit or expose credentials or fall back to an ambient identity. Developer tooling profile use does not move general product AWS-profile import/SSO into MVP.

Do not delete or overwrite existing shared manual data. Connection discovery must not upload probe objects or create resources. Obtain approval for a concrete test scope before destructive operations or provisioning dedicated integration-test resources. Future writes need explicitly authorized isolated fixtures; a unique prefix alone is not authorization. Cleanup uses an ownership manifest and known upload IDs, never blanket deletion or abort-all.

Production requires HTTPS with normal hostname/chain validation. Any explicitly authorized plaintext laboratory exception must be restricted to private development/test tooling and recorded locally. Never send signed requests through an unrelated proxy, follow authenticated redirects to an unapproved host, or turn a test exception into a shipped trust bypass. Plaintext connectivity cannot supply TLS/enterprise-CA evidence; qualify that separately using authorized HTTPS targets and controlled fixtures.

### Evidence publication boundary

Keep local environment configuration and execution records under gitignored `docs/internal/`. Do not link public documents to private files. Published qualification results must be separately sanitized and reviewed: retain the relevant provider build/configuration, product/SDK revision, test cases, outcomes and limitations while removing infrastructure identifiers, credential references, object names/content, account identifiers and sensitive request/response data. CLI-only connectivity evidence must remain distinct from application/SDK qualification. No family-wide Squid support claim follows from one tested configuration.

### Planned opt-in integration cases

After implementation authorization and approval of the required isolated resources/write scope, qualify upload/download round trips with byte-length and compatible checksum comparison; zero-byte and Unicode/exact-key cases; enough owned keys to cross pagination boundaries; multipart part retries, cancellation and retained cleanup obligations; and ambiguous completion/restart recovery under ADR-006. Overwrite/concurrent-writer tests must use only disposable test-owned objects, never existing manual data. Permission tests need separately authorized restricted identities/policies for direct-bucket access, denied ListBuckets, read-only/write-only, denied read-back and denied abort. Permission setups and writes require their own authorization. Keep fault injection isolated from shared networks and services.

## Local service selection

Proposed first local fixture: a pinned Adobe S3Mock container for basic transfer/multipart tests, plus a deterministic fault server for response edge cases. Its documentation says path-style only, unvalidated presigned URLs, and nonproduction security behavior. Do not use it as an authentication, virtual-hosted DNS, or Ceph admin oracle. Trust a test CA through the test harness; do not copy documentation examples that disable TLS verification into BucketBus. [S3Mock](https://github.com/adobe/S3Mock)

LocalStack is an alternative for broader AWS-shaped fixtures; evaluate current licensing/auth-token requirements, CI availability and operation coverage before choosing it. It still does not establish Ceph behavior. [LocalStack overview](https://docs.localstack.cloud/aws/getting-started/), [S3 coverage](https://docs.localstack.cloud/aws/services/s3/)

Do not adopt an old MinIO image as an unexamined default: the upstream community repository is archived as of the research date. A pinned historical build might be an isolated regression fixture, but would not establish ongoing supported service behavior. [MinIO repository](https://github.com/minio/minio)

The product itself has none of these container/tool dependencies. A developer-side Ceph sandbox may use administrative tools during authorized provisioning; the shipped desktop application does not.

## Requirement-to-test map

| Requirements | Essential cases | Required evidence |
| --- | --- | --- |
| F01, N01/N05 | Clean ZIP launch, no installed runtime, no admin, read-only application directory, non-ASCII executable path | Windows VM; measured archive/startup |
| F02–F05 | Valid/invalid key, wrong secret, token expiry, wrong region, direct bucket, restricted prefix, ListBuckets denied, TLS failures, both addressing styles | Fixtures plus real Ceph and AWS |
| F02/F03 | Export contains no secrets/references; hostile import cannot bind vault entries; locked/missing vault and declined unlock | Windows Credential Manager and Linux Secret Service integration |
| F06–F08 | Directory traversal, empty dirs/prefixes, folder markers, `a` plus `a/b`, huge local directory, symlinks, changing source, denied local directory | Pure tests plus both OS filesystems |
| F07/N03/N06 | Empty and multi-page listing, repeated/invalid tokens, pages smaller than request limit, cancellation, refresh changes, malformed XML, millions of synthetic keys | Fixtures; real large Ceph run before scale claim |
| F08–F12 | 0-byte, small, boundary-size, multipart files; part failures, delayed completion, lost completion response, retry exhaustion, checksum mismatch | Fault server plus real Ceph and AWS |
| F09–F11 | Process termination at every journal/network boundary, durable uncertain outcomes, interrupted enumeration, explicit restart confirmation, no silent replay, cleanup denied, corrupt journal, disk full | Local crash tests; actual upload cleanup and reconciliation on identified Squid build/AWS |
| F12/F13 | Safe temp-file commit, disk full, no-replace conflict, concurrent writer, 409/412, unsupported preconditions, 403 HEAD | OS tests plus real providers |
| F14/F18 | Mixed delete success, uncertain delete, versioned/suspended/unversioned buckets, delete markers, object lock/access denial | Real providers; no version purge in MVP |
| F15/F17 | Missing metadata, unsupported fields, XML/JSON error body, non-XML proxy error, request IDs, log injection/secret-bearing responses | Fixtures and provider captures with synthetic data |
| F16/N08 | Entire workflow by keyboard and screen reader, pane focus, context menus, external local drop, large selection, light/dark/high contrast, mixed DPI | Windows desktop and named Linux session |
| N02/N04 | Slow network with four transfers, 10 GiB files, queue enumeration larger than memory cache, cancellation | Measured desktop runs, record limits and resources |
| N07/N09 | Threat-case regression suite, dependency scans, SBOM/license inventory, signed extracted artifact, provenance verification | CI plus release VM |
| F05/F09–F13/F17, N02 | LAN and impaired WAN/VPN, disconnect/reconnect, latency/loss, timeouts, retry/backoff bounds, bandwidth limits, cancellation/cleanup and TLS trust | Network matrix below; deterministic faults plus actual authorized network/provider checks |

Authentication cases must test a real rejecting service, not a fixture that accepts every key. Policies should include read-only, write-only, exact-prefix access, missing abort permission, missing versioning-read permission, and explicit deny. A denied ancillary request must not incorrectly disable an otherwise allowed workflow.

## Adversarial data and transfer tests

Use synthetic keys: leading `/`, `../`, backslashes, drive/UNC/device syntax, `CON`, `A`/`a`, trailing dots/spaces, colon/alternate-stream names, percent sequences, plus signs, repeated slashes, combining Unicode, bidi/control characters, overlong components, and file/prefix collisions. Verify exact remote identity survives display/selection and no write escapes the selected local root. Exercise junction/symlink replacement between validation and write, including pre-existing temporary-file names.

Simulate connection loss before initiation, after initiation before journal recording, mid-part, after the last part, during completion and after commit before response receipt. Distinguish failure, uncertainty, confirmed completion and cleanup obligations. Test completion errors embedded in HTTP 200; SDK handling must reach the UI correctly. [AWS completion semantics](https://docs.aws.amazon.com/AmazonS3/latest/API/API_CompleteMultipartUpload.html)

Test growing/truncated/replaced upload sources and changed remote objects during download. Object size alone is never enough to resume or reconcile. Integrity tests must distinguish transport/checksum verification, full-object comparison, and unavailable verification. No universal ETag-MD5 assumption.

Exercise cancellation in preparation, enumeration, conflict waiting, retry backoff, streaming and verification. Repeated cancellation must not affect another job's files or upload. Never delete all bucket multipart uploads as test cleanup; use recorded IDs and dedicated test prefixes.

## LAN and WAN/VPN qualification

These are required qualification scenarios. Session 3 adds controlled offline request/body timeout, retry, cancellation and untrusted-certificate cases; full network/VPN/enterprise-trust qualification below remains **not run**. Use local deterministic transport faults or an isolated network-impairment harness when implementation/testing is authorized. Do not change shared or production network infrastructure. Emulation validates controlled behavior; it does not replace a check through an actual authorized VPN route. Record latency, loss, available bandwidth, outage duration, timeout values, SDK retry settings and observed attempt counts for reproducibility. Numeric impairment settings must be chosen during engineering review; none is a measured description of the user's network.

| Scenario | Planned stimulus | Required behavior / evidence |
| --- | --- | --- |
| LAN baseline | Stable local route, small and multipart objects below 1 GB | Establish throughput, responsiveness and request counts as a comparison baseline, not a product speed promise |
| Disconnect/reconnect | Break connection during read, part upload and write completion; restore before and after retry budget exhaustion | Bounded retries, responsive cancellation, durable uncertain outcome when needed; reconnect never silently replays an uncertain write |
| Latency and packet loss | Delayed responses, jitter, packet loss and retransmission in isolated fixtures | Measured retry/backoff bounds, no multiplied SDK/job attempts, no unbounded workers or misleading success |
| Request timeouts | Slow connection setup, delayed headers, stalled body and slowly progressing body | Distinguish failure-to-progress from a long but advancing transfer; finite reviewed deadlines; classify ambiguous mutations accurately |
| Limited bandwidth / long duration | Throttle sub-1-GB transfers; include larger synthetic stress files | Low bandwidth must not imply corruption or trigger an unjustified whole-file timeout; bound memory, keep progress/cancel responsive, account for credential expiry if temporary credentials are included |
| Multipart cancellation / cleanup failure | Cancel with parts in flight; disconnect or deny abort; reconnect and restart the application | Persist the job's cleanup obligation, retain upload identity when known, do not mark cleanup complete prematurely or abort unrelated uploads |
| VPN reconnect / reachability changes | Tunnel unavailable/restored, route/DNS changes, endpoint unreachable then reachable | Revalidate TLS identity and reachability without silent credential/endpoint substitution; continue only operations proven safe, and retain unresolved write outcomes |
| TLS and enterprise CA trust | Valid enterprise-CA chain via approved trust store, untrusted/expired/wrong-host certificate, changed certificate after reconnect | Accept only valid trusted identity; clear failure and no bypass. Test CA setup is confined to the authorized test environment |

Proxy authentication and TLS interception are separate unresolved requirements. Add their specific scenarios only after Q09 confirms the need/configuration; VPN presence alone does not establish either. Enterprise-CA trust tests remain required regardless.

Workload fixtures should prioritize realistic files under 1 GB, plus zero-byte, multipart-boundary and larger synthetic cases. Retain the proposed 10 GiB/million-key stress tests as engineering targets, not observed workload maxima. Collect retransmitted bytes, elapsed time and any estimated transfer cost with assumptions clearly labeled. Review those measurements and user feedback for ADR-006's revisit triggers: large objects, frequent WAN interruptions, unacceptable retransmission cost, repeated complaints. No automatic telemetry is introduced.

Crash/replay acceptance: an interrupted in-flight mutation must recover as unresolved until evidence establishes its outcome. User confirmation to restart cannot itself clear that state. Assert that no subsequent write is sent silently while uncertainty remains; test denied read-back, changed destination, versioned buckets and exhausted retry budgets. Verify durable recovery when the crash occurs before/after intent commit, upload-ID recording, remote completion and local completion recording. Review SDK-internal retries as part of this contract, not merely the visible application retry button.

## Ceph qualification

Use Squid (19.x) as the primary initial Ceph S3 target. Record the exact patch/build, package provenance, backports, configuration and authorized isolated test scope before 1B write qualification; before v1.0, run the relevant S3 and LAN/WAN/VPN qualification against that identified target using disposable data. Scope compatibility statements to tested builds/configurations. Record additional targets only when selected and exercised. Before Phase 3 release, require at least two selected builds spanning the identity models being claimed, plus configured capabilities and tenant/account fixtures. Squid selection does not move legacy Admin Ops, account IAM administration or future Dashboard integrations into MVP.

For Admin Ops: enumerate users completely; create/read/modify/suspend/reactivate; create/revoke keys; verify secret redaction; read/write quotas at distinct scopes; inspect usage enabled/disabled; verify rate-limit units and enforcement context; test denied capabilities and stale statistics. Test rotation with one consumer still on the old key to ensure the UI does not imply migration completion.

For IAM: test account root/delegated user, users/groups/roles, supported policy attachment variants, account versus tenant names, explicit deny, STS expiry, wrong endpoint signing, and operations absent in the selected release. Legacy roles do not prove account-user IAM support.

Multisite requires an actual multi-zone environment. A single RGW container cannot validate replication lag, remote-zone visibility, promotion behavior, or sync-status completeness. Dashboard API tests need separate authentication and version contracts. No destructive repair or topology mutation in the initial visibility test suite.

## CI and release pipeline proposal

1. Pull requests: Linux/Windows build and behavior tests, formatting/static analysis, dependency checks, secret scanning, deterministic fixtures and redaction tests. Use unprivileged jobs without release/cloud secrets for untrusted contributions.
2. Trusted integration jobs: local container tests; live Squid jobs only through explicit opt-in and separately approved isolated resources/write authority. Shared manual-testing data is excluded from unattended CI fixtures. Pin images by digest; retain exact patch/build, packaging/config and network-path metadata. Do not contact production.
3. AWS validation: separately authorized dedicated account, scoped roles, explicit test prefixes, storage/request budgets, lifecycle backstops and cleanup ledger. Full large-scale runs require budget approval; ordinary PRs do not launch them.
4. Release candidate: all claimed provider rows, clean Windows standard-user VM, supported Linux package test, accessibility/DPI, performance budgets, dependency/license disposition and reproducibility comparison.
5. Release: protected build/signing workflow, tag and source revision, signed Windows payload, checksums, SBOM, provenance, compatibility table and limitations. Publication requires the later release authorization; none is granted by this plan.

Measure p95 targets under PRD's reference conditions; publish actual hardware and samples. A million-key fixture proves bounded client behavior, not million-key gateway performance. Cloud cost and cleanup are test design constraints, not reasons to invent results.

## First internal alpha implications

After separate implementation authorization, an early development alpha may use explicitly opted-in read-only checks against an authorized Squid target, plus Fedora/Docker fixtures for deterministic network faults. Real transfer tests need approved isolated write fixtures and AWS access for the 1B gate. Connectivity checks alone do not complete 1B or establish provider compatibility. Complete Windows 11 VM evaluation during 1A before claiming Windows execution/packaging checks; a Fedora-only build is development evidence only.

Prioritize sub-1-GB file browsing/transfers, multipart-boundary cases, explicit restricted-bucket flows, bounded retries and honest interrupted/uncertain states. The first alpha that exposes restart recovery must demonstrate durable records, reconciliation gating, explicit restart confirmation and cleanup obligations in fixtures; it must not offer an unsafe automatic retry while those paths are unfinished. Full ADR-006 qualification remains the 1C gate. Partial-byte cross-restart resume is not an alpha or MVP prerequisite.

Use alpha observations to refine timeout/backoff values and retransmission assumptions. Mock success cannot establish Squid authorization, conditional-write/checksum compatibility or real VPN behavior. Real Squid/AWS workflow evidence remains required for 1B and release qualification, and larger stress cases remain planned despite the smaller typical workload.

## Evidence record and stop conditions

Every provider qualification records product commit, SDK version, OS/build, endpoint type, provider version/config, test-case revision, execution time, result, sanitized evidence, and cleanup result. Use **not run**, **pass**, **fail**, or **blocked**. Documentation-only observations remain separate.

Block release on silent data corruption, path escape, unapproved overwrite/delete, credential leakage, insecure TLS bypass, incorrect success reporting, or failure on a required claimed platform/provider. Failed tests cannot be hidden by deleting them or silently shrinking the compatibility claim. Any deliberate scope change belongs in [DECISIONS](DECISIONS.md).

## Session 2 executed coverage

[Session 2 evidence](SESSION2_IMPLEMENTATION.md) records profile/schema/atomic-update tests, fake-backed credential failure injection, Windows buffer handling, Secret Service prompts/cancellation, and headless profile-editor behavior. The native vault lifecycle test requires explicit opt-in and disposable application-owned entries with cleanup; it skips before native access by default. Linux interactive vault/desktop and Windows native checks are BLOCKED, not passed. All tests remain independent of the Ceph lab and existing AWS CLI credentials.

## Session 3 executed coverage

[Session 3 evidence](SESSION3_IMPLEMENTATION.md) records 52 new actual-SDK tests plus the 82 preserved tests. Synthetic transports cover exact keys, signatures, nullable fields, direct-bucket access, opaque pagination, malformed responses, bounded retries, deadlines, cancellation and stale sessions. A local TLS listener proves rejection of an untrusted certificate without a trust override. Tests make no external S3 calls and use synthetic credentials. No live provider compatibility, Windows execution or native vault success is inferred.

## Session 4 executed browser regression coverage

[Session 4 evidence](SESSION4_IMPLEMENTATION.md) records 34 new tests, including real SDK-to-view-model flows and Avalonia control/keyboard checks. Generation tests deliberately complete obsolete work after navigation/profile/selection changes. A million-object synthetic traversal verifies the five-page cache and eviction without prefetch; virtualization limits realized list containers. These are offline/headless results, not native platform, live provider or network performance qualification.

## Session 5 executed qualification

[Session 5 evidence](SESSION5_IMPLEMENTATION.md) preserves 168 offline passes and adds 14 integration guard passes. The disabled live case skips before external I/O. Two explicitly opted-in read runs passed against existing and separately generated Ceph fixtures. Updated user authority permitted one dedicated fixture bucket and 1,008 PUTs; all owned keys/bucket were cleaned up with absence confirmed, preserving shared manual data. This fixture tooling is separate from the GET/HEAD-only .NET harness and shipped application. Native desktop/vault and broader live faults remain pending; Linux locked self-contained publish passes. Final infrastructure teardown remains unauthorized.

## Phase 1B.2 transfer coverage

Offline transfer tests exercise no-replace destination races, source replacement/
modification, symlink refusal, zero-byte/Unicode data, short reads, checksum mismatch,
streaming with a generated 32 MiB body and 64 KiB reads, disk-full failures,
cancellation/deadlines, conditional PUT serialization, unknown outcomes and bounded
read-back without replay. Headless tests cover keyboard actions, independent browsing,
terminal states, refresh and cancellation on close. Native Windows execution remains
BLOCKED pending the VM. Live write qualification is separate from the read-only
harness and requires independent endpoint and bucket/prefix approvals; see
[LIVE_QUALIFICATION](LIVE_QUALIFICATION.md#single-file-write-qualification).
