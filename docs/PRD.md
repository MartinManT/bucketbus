# BucketBus product requirements

Status: DRAFT for review · 2026-10-21 · Scope baseline: [decisions](DECISIONS.md)

## Release definition

Phase 1 produces an internal alpha and then a public v1.0 candidate. The alpha may use a local mock. Public v1.0 requires Windows 11 x64, real Ceph RGW, and real AWS S3 general-purpose bucket validation. Squid (19.x) is the primary initial Ceph validation family. Record exact package/build provenance, configuration, authorized isolated test scope and OS builds for qualification. Passing an emulator or CLI connectivity check cannot satisfy those release gates or establish compatibility with all Squid deployments.

MVP below contains confirmed requirements, the approved ADR-006 recovery scope, and proposed engineering details. The user has confirmed the core workflow and modular application approach; other unapproved behaviors and numerical budgets still await review. Requirement IDs are stable references for later implementation and tests. Approval is not evidence of implementation or testing.

The [Phase 1A engineering handoff](PHASE1A_PLAN.md) now includes secure connection profiles and read-only bucket/object/metadata browsing alongside platform feasibility, as requested. The first development alpha exposes these read-only capabilities; upload/download and durable recovery remain 1B/1C. This sequencing change does not remove any MVP safety requirement or authorize coding.

## Confirmed workload and environment inventory

Typical transfers are under 1 GB, across both LAN and WAN/VPN. This is confirmed workload context, not a hard size limit or a measured distribution. Maximum file size, object counts and interruption frequency remain unmeasured. Fedora with .NET 10 SDK and Docker is the development baseline. Windows 11 testing is a Phase 1A prerequisite. Enterprise proxies and TLS interception are unresolved requirements, not assumed network features. Any explicitly authorized plaintext laboratory exception belongs only to private development/test tooling; production requires HTTPS. Developer credential-profile use does not commit MVP to product AWS-profile import. [Live integration safety](TEST_STRATEGY.md#live-integration-access-and-safety) defines the test boundary.

ADR-006 approves durable queued/interrupted jobs, accurate uncertain outcomes, bounded safe retries, in-session multipart-part retries and recorded cleanup obligations. After restart, reconciliation and explicit user confirmation precede restarting the object from the beginning. Cross-restart partial-byte resume is desirable but deferred to Phase 2. Preserve F12/F13 integrity and overwrite protections; no silent replay of uncertain writes. Revisit the decision if large objects, frequent WAN interruptions, unacceptable retransmission costs or repeated complaints emerge.

## Functional requirements and acceptance

| ID | Requirement | Acceptance criterion |
| --- | --- | --- |
| F01 | Portable startup | A standard Windows user extracts the ZIP and launches on a clean supported VM without .NET, AWS CLI, Python, Ceph CLI, or an installer. No service or elevation request. |
| F02 | Connection profiles | Create, edit, remove, import/export profiles containing name, HTTPS endpoint, signing region, addressing mode, optional bucket and prefix, and non-secret preferences. Export excludes access keys, secret keys, session tokens, credential references, and administrative bindings. Import never connects automatically. |
| F03 | Authentication | Explicit static access/secret key selection; optional supplied temporary access/secret/session-token triplet proposed for MVP. Secrets remembered only by explicit choice. Expired credentials pause affected jobs with an actionable prompt. |
| F04 | Restricted access | A profile with a specified bucket bypasses account-wide ListBuckets. A prefix restriction is honored in navigation requests. An access-denied response is distinct from an empty list. No write probes during connection. |
| F05 | Addressing and TLS | SigV4, explicit signing region, path-style and virtual-hosted-style options. Validate HTTPS hostname and trust chain. Support enterprise CAs through approved OS trust configuration. No ignore-certificate switch. |
| F06 | Local pane | Browse drives/directories and file metadata, sort, select multiple items, refresh, and choose a destination. Permission failures and disappearing files do not crash the UI. Symlinks are visible and skipped during recursive transfer by default. |
| F07 | Remote pane | Navigate buckets and prefixes; distinguish object rows from prefix rows, including simultaneous keys `a` and `a/b`. Load pages on demand. No eager whole-bucket traversal. Show exactly what search/sort covers. |
| F08 | Transfers | Upload/download files and explicitly selected directory/prefix trees. Tree copying is one-way enumeration plus transfers, with no comparison-based synchronization or deletion propagation. Preserve hierarchy where names are representable; stop or ask on mapping conflicts. |
| F09 | Queue | Show source, destination, state, useful progress, bytes, rate, and error. Bounded concurrent workers and bounded listing buffers. Queue survives restart as interrupted jobs requiring review; automatic execution after restart is forbidden. |
| F10 | Multipart | Automatically choose multipart above a configurable tested threshold. Retry failed parts within the running session where replay is safe. Persist known upload IDs and cleanup obligations. Cancellation requests abort only that job's upload. Report incomplete cleanup separately. Recovery scope approved in ADR-006; tuning remains proposed. |
| F11 | Recovery | Approved ADR-006: persist accurate uncertain outcomes; retry transient failures within bounded limits. Failed auth/TLS/permissions require intervention. Never silently replay uncertain writes. Reconcile outcomes, obtain explicit user confirmation after application restart, perform required cleanup and revalidate source/conflict safety before restarting the affected object from the beginning. Confirmation alone does not resolve uncertainty. Partial-byte restart resume is deferred to Phase 2. |
| F12 | Integrity | Never expose a partial download under its final name. Validate available compatible checksums and byte length. A checksum failure is not success. Report verification level when a provider cannot supply a usable checksum. Never equate every ETag with MD5. |
| F13 | Conflicts | Ask before replacing known existing data; offer Skip, Choose another name, and Replace. Use conditional writes where validated. Never silently fall back from protected create/replace to unconditional overwrite. See architecture for unsupported-provider behavior. |
| F14 | Deletion | Confirm deletion of explicitly selected object rows, showing endpoint, bucket, exact keys, and count. MVP excludes prefix-recursive deletion, bucket deletion, and version-specific purge. Record each result independently. |
| F15 | Metadata | Show size, modification time, content type, ETag, and available object metadata on demand. Unknown fields stay unknown; do not issue HEAD for every visible row. Editing remote metadata is deferred. |
| F16 | Input and accessibility | Internal pane drag/drop, keyboard-only equivalent transfers, context menus, persistent focus, light/dark themes, and high-DPI layout. External local-file drops are a proposed Windows MVP feature; remote drag-out to Explorer is deferred. |
| F17 | Diagnostics | Explain auth failure, access denial, wrong region, DNS/TLS failure, expired token, full disk, unavailable object, checksum mismatch, partial batch failure, and unknown outcome. Redacted diagnostics include safe provider error codes/request IDs. |
| F18 | Version-aware safety | Ordinary listing/transfer works against versioned buckets without a history browser. Deletion wording explains that versions may remain; a version-suspended bucket may behave differently. If versioning cannot be read, show “versioning state unknown.” Never promise undelete. |

For AWS, ListObjectsV2 returns at most 1,000 entries and requires bucket-list permission; directory buckets have distinct behavior. This motivates separate direct-bucket access and excludes S3 Express/directory buckets from the initial compatibility claim. [AWS ListObjectsV2](https://docs.aws.amazon.com/AmazonS3/latest/API/API_ListObjectsV2.html)

## Scope decisions and review status

Rows remain proposed except the recovery scope explicitly approved under ADR-006. The journal technology and numerical retry/timeout settings are not approved by that scope decision.

| Capability | Phase | Reason / status |
| --- | --- | --- |
| Basic overwrite protection and ambiguous-outcome reconciliation | 1 | Data correctness, not an advanced convenience. |
| One-way selected folder/prefix copy | 1 | Necessary for the expected file-manager workflow. |
| Manually supplied temporary session credentials | 1 | Avoid a static-key-only data model; no login or refresh orchestration required. |
| Durable jobs, uncertain-outcome records and multipart cleanup obligations | 1 | Approved ADR-006; SQLite is conditionally recommended by engineering review; selection approval and durability validation remain open. |
| Resume partial uploads/downloads after process restart | 2 | Deferral approved in ADR-006; desirable, not mandatory for MVP. Revisit against actual retransmission burden. |
| AWS profile discovery, AssumeRole, STS acquisition, SSO | 2; earlier if launch environments require | Explicit profile selection and trustworthy refresh flows need separate design. No ambient credential chain in MVP. |
| S3-to-S3 copy; rename as copy then delete | 2 | Provider limits, cost, metadata, ownership, and partial failure need independent flows. |
| Version browsing/restoration, tagging, presigned URLs | 2 | Useful additions after safe core transfers. URLs are bearer secrets. |
| SSE selection and storage-class selection | 2 | Honor server defaults in MVP. Buckets requiring explicit encryption headers may not be compatible until implemented. |
| Bucket policy, ACL, lifecycle and versioning configuration | 2 | These are standard S3 management features, even when used on Ceph; require privileged-change UX. |
| Bandwidth limiting | 2 | Bounded concurrency first; bandwidth policy if demand supports it. |
| Recursive synchronization and recursive deletion | 2 or later | Separate preview/confirmation design; not implicit in folder transfer. |
| RGW users/keys/quotas; account IAM | 3 | Separate administrative authorities and release qualification. |
| Multisite diagnostics and organization identity integration | 4 or demand-driven earlier | Additional API/authentication contracts; no cluster access in ordinary transfer client. |

## Non-functional targets

These are proposed budgets, not benchmark results. Proposed reference measurement environment: Windows 11 x64 VM, 4 vCPU, 8 GiB RAM, SSD-backed disk, release build, no debugger. This VM is not yet provisioned. Record host, VM, antivirus, cold/warm cache, and filesystem details. Linux gets its own recorded baseline; do not compare RSS directly with Windows private working set. The 10 GiB and million-key cases below are synthetic stress targets, not claims about typical user workloads.

| ID | Target | Measurement and consequence |
| --- | --- | --- |
| N01 | Shell usable within 3 s p95 | 20 cold launches; network connection excluded. Measure before adding optimizations. |
| N02 | Visible feedback within 100 ms; cancellation acknowledged within 1 s | Slow endpoint and large enumeration test. Request teardown/remote cleanup can take longer and remain visible. |
| N03 | A received 1,000-row page displayed within 250 ms p95 | Render/update time, excluding network. Virtualized rows and bounded cached pages. |
| N04 | Idle memory ≤250 MiB; four active transfers ≤512 MiB | Proposed process private-working-set budget on reference Windows VM, including native allocations. Stream 10 GiB files; memory must not scale with file size. |
| N05 | ZIP ≤150 MiB; expanded distribution ≤350 MiB | Initial Windows x64 goals. Revise openly after a measured packaging evaluation, not through premature AOT/trimming. |
| N06 | Millions of keys remain navigable with bounded memory | Simulated million-key pagination early; real large-bucket run before publishing a tested scale claim. UI must not imply global size-sort or total counts without enumeration. |
| N07 | No silent data loss | Zero accepted cases of unintended overwrite, escaping destination root, false successful transfer, or undocumented partial deletion. Any such defect blocks release. |
| N08 | Keyboard and assistive-technology usability | All core tasks with keyboard and Windows Narrator/NVDA; Linux Orca validation required before claiming Linux accessibility. No color-only status. |
| N09 | Auditable releases | Dependency inventory, licenses, checksums, source revision, build provenance, vulnerability disposition, and signed public Windows artifacts. Signing route is an open prerequisite. |

## Public v1.0 acceptance gate

All F01–F18 behaviors in the approved MVP scope must have traceable tests; revised scope requires an explicit decision. N01–N06 budgets require measurements and documented disposition; N07–N09 cannot be waived by relabeling a failure as a performance tradeoff. Actual Ceph/AWS/Windows runs are mandatory. Unknown test outcomes remain unknown, not “supported.”

There must be no unresolved critical/high security defect, no known silent corruption or destination-escape defect, and no failed ordinary restricted-bucket workflow on a claimed provider. Obtain the named validation environments before scheduling a public release.

The network qualification must cover LAN and WAN/VPN: disconnection/reconnection, latency, loss, request timeouts, bounded retries/backoff, multipart cancellation/failed cleanup, VPN reachability changes, enterprise-CA trust, and limited-bandwidth long-running transfers. Exact impairment profiles and timeout values require engineering review; see [test strategy](TEST_STRATEGY.md). A transfer under 1 GB must not be assumed short-lived or immune to these failures.

## Current pre-alpha transfer increment

Phase 1B.2 provides one-file upload/download actions and keyboard equivalents, observable bytes/progress, cancellation and Completed/Failed/Cancelled/Outcome Unknown states. Uploads attempt conditional creation with explicit profile credentials and full read-back integrity, without an external qualification-file prerequisite. Remote collision protection depends on provider enforcement; unsupported requests fail without unconditional replacement. Downloads refuse existing destinations and unsafe names. Local and remote browsing remain independent. No batch/multipart transfers or crash-recovery promise is exposed. The durable MVP requirements above remain binding for Phase 1C.
