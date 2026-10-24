# BucketBus decisions, open questions, and risks

Status: DRAFT decision register · 2026-10-21

This register separates **confirmed user requirements/inventory**, **approved design decisions**, **proposed engineering choices**, **unresolved validation requirements**, and **executed test evidence**. User-reported inventory is not a measured test result. Approval of one decision does not approve every downstream detail, and approval of this documentation never authorizes production implementation by itself. The document set remains DRAFT while individual decisions can be approved. [Session 1](SESSION1_IMPLEMENTATION.md) passed offline foundation/shell tests; no SDK provider qualification has been performed. Local connectivity evidence is maintained separately from public planning and does not establish application compatibility.

## Confirmed product constraints

- **C01:** General-purpose S3-compatible desktop client; Ceph first in development/validation; both Ceph and AWS required for public v1.0. Other providers require tests before support claims.
- **C02:** Windows 11 x64 ZIP, no installation/elevation/separate runtime/service; shared practical Linux codebase. Per-user state is allowed; stateless USB operation is not required.
- **C03:** OS-protected saved credentials; profile exports exclude credentials by default; portable encrypted vault deferred.
- **C04:** Static keys, explicit endpoints, SigV4, both addressing styles, valid HTTPS, restricted/read-only credentials, and direct bucket access are core requirements.
- **C05:** Ceph administration is a later separate workspace and authority. No SSH dependency, no assumption that RGW keys equal cluster privileges.
- **C06:** Apache License 2.0, minimal unnecessary dependencies, no telemetry by default, auditable releases.
- **C07:** Session 1 implementation (T01–T03) explicitly approved after engineering review; later sessions require separate approval. No commits/pushes, infrastructure changes, live lab access, credential persistence or later features are authorized by this session. Local access authority is separate from implementation approval.
- **C08:** Architecture B selected by the user: one desktop process, small internal modules for Avalonia UI, core workflows, S3 and OS services; separate Ceph clients later.
- **C09:** Confirmed workload/development inputs: typical files under 1 GB; Squid (19.x) as the primary initial Ceph validation family; LAN and WAN/VPN as target networks; Fedora with .NET 10 SDK and Docker as the development baseline. Windows 11 VM evaluation is a Phase 1A prerequisite. Exact provider builds/configurations and authorized test scopes need qualification records, maintained separately from public planning. Typical size is not a maximum supported file-size limit; maximum size, object counts and interruption frequency remain unmeasured.

## ADR-001: Small modular desktop application

**Status:** Confirmed approach; detailed project partition proposed.

**Context:** Keep UI, transfer policies, S3 details, OS services and future administration maintainable without a platform framework.

**Decision:** Four initial projects described in [architecture](ARCHITECTURE.md), one process and composition root, no dynamic plugin loading. Future administration contracts are separate and added when used.

**Alternatives:** Single project with folders was smaller but easier to couple; runtime plugin host imposed unneeded lifecycle/security contracts. User selected the middle approach.

**Consequences:** Some interface/assembly structure now; no process isolation. Revisit process separation only for a demonstrated trust or reliability boundary.

## ADR-002: Retain the preferred managed desktop stack

**Status:** Proposed.

**Decision:** C#/.NET 10 LTS, a pinned stable Avalonia 12.1-line evaluation, MVVM, AWS SDK .NET v4. Keep SDK types out of Core and view models. A small maintained MVVM helper may be selected; avoid adding a second reactive/state framework without need.

**Why:** Fits developer preference and shared Windows/Linux delivery. Alternatives and sources are in architecture. Framework platform/accessibility claims need real tests.

**Consequences/revisit:** Measure startup, package, memory and assistive technology at milestone 1A. A failed essential gate triggers a framework comparison, not an automatic waiver. No performance result or exact package pin is asserted now.

## ADR-003: Self-contained ZIP with multiple files

**Status:** Proposed within confirmed portability requirements.

**Decision:** Ship a self-contained Windows x64 publish folder in a ZIP; Linux bundles for qualified environments. Do not make single-file, trimming or AOT a launch requirement.

**Why:** No installed runtime is needed; native libraries and notices stay explicit. Fewer extraction/reflection concerns before measurements.

**Consequences:** Runtime security fixes require refreshed product artifacts. Linux OS library prerequisites still exist. Revisit optimizations only after measured need.

## ADR-004: Explicit credentials and authorities

**Status:** OS storage and admin separation confirmed; session credentials/detail proposed.

**Decision:** Windows Credential Manager and Linux Secret Service; memory-only fallback with explicit user choice. Accept manually supplied session-token credentials in MVP, memory-only. No automatic AWS default credential chain; profile selection and role/SSO acquisition later.

**Why:** Arbitrary endpoints must not receive ambient credentials. Session credentials prevent an unnecessarily static-only core model without implementing a login platform.

**Consequences:** No cross-machine secrets portability; sessions may require reentry. If actual launch users mandate SSO, promote the needed flow before scope approval.

## ADR-005: S3 semantics and evidence-based capabilities

**Status:** Proposed elaboration of confirmed object-storage requirement.

**Decision:** Exact keys, prefix presentation, paged virtualized lists, bounded caches, explicit partial listing/sort scope. Track feature evidence separately from observed authorization. No write probes during connection.

**Consequences:** No global size-sort/search without enumeration; no generic atomic rename promise; 403 is not unsupported or nonexistent. Provider profiles encode tested behavior, not product-name guesses.

## ADR-006: Recovery before advanced resume

**Status:** Approved MVP scope decision by the user on 2026-10-21. Not implemented or validated. The persistence mechanism, retry/timeout settings and reconciliation algorithms remain engineering-review items.

**Decision:** Persist queued and interrupted jobs, accurate uncertain-outcome records, and known multipart cleanup obligations. Retry transient failures within bounded limits and retry failed multipart parts during the running session only where safe. Reconcile uncertain outcomes before restarting a transfer; never silently replay an uncertain write. After application restart, require user confirmation before restarting interrupted jobs, then restart the affected object from the beginning after reconciliation, required cleanup and renewed conflict/source checks. Defer cross-application-restart partial-byte upload/download resume to Phase 2.

**Why:** The user reports typical files under 1 GB across LAN and WAN/VPN, and considers cross-restart partial-byte resume desirable but not mandatory for MVP. This sufficiently resolves Q02 for the scope decision, without claiming measured maxima or interruption rates. The approved model retains integrity and safe overwrite protection while avoiding premature cross-restart checkpoint/range-resume complexity.

**Safety contract:** Preserve integrity checks, per-operation replay-safety (idempotency) analysis, conditional overwrite behavior and explicit ambiguous-write outcomes. A reconnect or timeout does not establish that a write failed. Confirmation to restart is not a substitute for reconciliation and does not authorize discarding an unresolved outcome. Repeated operations must not be assumed harmless, especially with versioning or a changed destination. Keep unresolved records durable until the outcome is established or the operator explicitly disposes of the case with its uncertainty visible.

**Alternatives:** In-memory queue is simpler but loses recovery context. Full persistent multipart/download resume avoids retransmission but adds a larger integrity and compatibility contract.

**Consequences/revisit:** Some interrupted objects will be retransmitted in full; cancellation cannot guarantee immediate remote cleanup while offline. Revisit if observed workloads contain materially larger objects, WAN/VPN interruptions are frequent, retransmitted bytes/time/cost become unacceptable, or users repeatedly complain about restart recovery. Review these signals during internal-alpha feedback and before public v1.0; bring partial resume forward only through an explicit scope revision. Gather opt-in observations or synthetic test evidence without adding telemetry by default. No numeric revisit threshold has been measured or approved.

## ADR-007: JSON settings plus SQLite journal

**Status:** Preferred engineering choice; engineering review conditionally recommends SQLite, with selection approval and implementation validation still pending. Approval of ADR-006 does not approve a particular journal implementation. See [Phase 1A review](PHASE1A_PLAN.md#transfer-and-sqlite-review-requirements-for-1b1c).

**Decision:** Atomic versioned JSON for settings/profiles; one SQLite dependency without ORM for durable transfer state and large manifests; single writer and per-user instance lock.

**Why:** Bounded memory and crash transactions justify a focused persistence dependency. Whole-queue JSON snapshots become awkward at scale.

**Consequences:** Native packaging, schema migration and corruption recovery must be tested. Journal contains sensitive identifiers but no credentials. It is not an encrypted vault.

## ADR-008: Protected overwrite and safe local commit

**Status:** Proposed, security-critical.

**Decision:** Use validated conditional writes; do not silently downgrade to unconditional replacement. Unsupported protection requires a disclosed explicit choice or refusal. Local output uses safe contained temporary files and tested no-replace/replace commit semantics.

**Consequences:** Some generic endpoints cannot offer the requested guarantee. Exact-name preservation may require explicit local mapping; no universal lossless filesystem representation is assumed. Fail closed if destination safety cannot be established.

## ADR-009: Narrow destructive scope

**Status:** Proposed.

**Decision:** MVP copies selected folder trees but deletes only explicitly selected object rows. No prefix-recursive deletion, bucket deletion, move/rename, synchronization or version purge.

**Why:** Copying a directory is a common workflow; automatic comparison/deletion and compound moves introduce different data-loss risks.

**Consequences:** Less remote housekeeping in v1.0. Later deletion/sync needs explicit manifests, previews, changed-source handling and per-item outcomes.

## ADR-010: Separate Ceph API families

**Status:** Separation confirmed; detailed future API mapping proposed.

**Decision:** Standard S3 management stays in S3; legacy user/quota/capability workflows use RGW Admin Ops; account identity uses IAM; optional multisite visibility may use a separate Dashboard HTTP adapter in Phase 4.

**Why:** Different authorities and contracts. HTTP-only Dashboard visibility is possible in documented deployments; this does not make it an RGW capability.

**Consequences:** No effective-permissions promise, no SSH/cluster key dependency, no full ownership migration claim. Every admin feature needs selected-release evidence.

## ADR-011: Release evidence and security start in Phase 1

**Status:** Proposed gate details within confirmed enterprise/security intent.

**Decision:** Real Ceph/AWS/Windows qualification, OS vault/TLS/accessibility, dependency inventory, SBOM/provenance/checksums and signed public Windows artifacts are launch foundations. Mock-based alpha is allowed, provider support claims are not.

**Consequences:** Unknown environments/signing resources prevent scheduling public v1.0. No calendar estimate until maintainers and validation capacity are known. Reproducibility is a goal until independently compared outputs exist.

## Open questions and defaults

Owner means the proposed person responsible for resolving it, not an assigned external task. Updated from the user's 2026-10-21 inventory and ADR-006 approval. “Resolved for scope” does not mean the validation work is complete.

| ID | Status | Confirmed/default and remaining question | Owner | Milestone deadline |
| --- | --- | --- | --- | --- |
| Q01 | Target family selected; qualification open | Squid (19.x) primary target. Record exact package/build provenance, backports, configuration, tenant/account model, restricted identities and isolated write-test authority per target; local access records are maintained separately; no family-wide support claim | Product owner + maintainer | Record configuration and approve isolated test scope before 1B write qualification; evidence before any Ceph support claim |
| Q02 | Resolved for MVP scope; measurement open | Typical files under 1 GB and LAN/WAN/VPN confirmed; ADR-006 approved. Maximum size, object counts, interruption frequency and retransmission burden remain unmeasured | Product owner + maintainer | Initial measurement/fixture plan before 1C; review alpha feedback and before 1E; does not block scope approval |
| Q03 | Open; proposal unchanged | Static keys required; manual temporary credentials proposed. Confirm whether SSO/role acquisition is mandatory for launch; acquisition otherwise deferred | Product owner | Before committing launch authentication scope; static-key 1A foundations can proceed after coding approval |
| Q04 | Package graphs verified; native validation open | Fedora locked builds/tests and Linux/Windows cross-publishing pass with exact dependencies. Windows restore/build/runtime and Fedora self-contained desktop/secret-service qualification remain open | Maintainer | T01 dependency validation; T05/T10/T12 native checks during 1A |
| Q05 | Distribution measured; runtime budgets open | Session 6 Windows ZIP/expanded sizes meet proposed N05 budgets. Native startup, memory, responsiveness and accessibility measurements/disposition remain open. Under-1-GB typical size does not remove large-file stress tests | Product owner + maintainer | Use proposed budgets for initial 1A measurement; explicit disposition before 1A exit; transfer/scale verification at 1C |
| Q06 | Environments pending | User reconfirmed Windows 11 VM unavailable in Session 6; T12 BLOCKED. AWS disposable account/access/budget unconfirmed | Product owner | Provision VM as a 1A action before Windows checks; AWS ready before real 1B runs; neither available by assumption |
| Q07 | Open | Signing identity, funding and protected publishing method | Maintainer | Before public v1.0 / 1E release |
| Q08 | Engineering recommendation complete; approval/proof open | SQLite conditionally recommended; local WAL/FULL, bounded worker, migration/corruption and commit-failure requirements documented; no implementation or durability evidence | Maintainer + product owner | Approve mechanism before persistence implementation; crash/migration/native-package acceptance at 1C |
| Q09 | Partially resolved | LAN/WAN/VPN and production HTTPS/enterprise-CA qualification required. Proxy use, proxy authentication, TLS interception and explicit encryption-header requirements remain unknown | Product owner + maintainer | Clarify before 1B network/auth qualification plan; test required configurations before claiming compatibility |
| Q10 | Open; proposal unchanged | Exact-key invalid-name policy: prompt/skip proposed; reversible bulk mapping later | UX/security review | Before download implementation at 1B |
| Q11 | Open | Developer capacity and maintenance/release response commitments | Maintainer | Before roadmap dates or enterprise support promises |

## Engineering review disposition and remaining validation

The requested gstack-plan-eng-review is complete; [findings, tasks and gates](PHASE1A_PLAN.md) recommend starting read-only Phase 1A after coding approval. Architecture B and ADR-006 are retained. The mechanisms below remain implementation/validation obligations, not executed evidence. Independent outside-review coverage was unavailable. No additional discovery cycle is required:

1. **Replay safety and SDK retries:** inspect automatic retries for each operation, particularly initiation/completion and ordinary PUTs. Establish how uncertain attempts become durable and how versioned/concurrently changed destinations are reconciled. Reconnect and user confirmation must not bypass unresolved-state gates.
2. **Timeout/backoff design for LAN/WAN/VPN:** separate connection, headers, stalled-body and total deadlines; permit slow progress with finite bounds. Verify one retry budget across SDK and scheduler, cancellation during backoff, reachability changes and retained TLS identity. Exact values remain proposed.
3. **Journal durability:** review preferred SQLite transactions, crash windows around remote effects, interrupted enumeration, migration/corruption recovery, sensitive metadata and per-user instance locking. State what survives process crash versus machine/power failure and test the claimed durability level.
4. **Identified Squid compatibility:** qualify checksum/trailer behavior, conditional writes, multipart cancellation/abort permissions and read-back under restricted credentials against an identified Squid build with its package provenance and configuration recorded. CLI reads do not validate SDK v4 writes. Do not inherit guarantees from Tentacle research or generic S3 mocks.
5. **Integrity and recovery interaction:** preserve stable source bytes for part retries, safe temporary-file commits, destination revalidation and cleanup obligations. Ensure a lost upload ID or denied reconciliation/abort remains visible rather than being reported as successful cleanup.
6. **Validation sequencing and budgets:** provision the Windows VM in 1A when authorized; define the development-alpha versus 1B/1C evidence gates; select impairment profiles and large-file stress coverage without treating the typical under-1-GB size as a hard cap. Use measured retransmission burden to assess ADR-006 revisit conditions.
7. **Live-test and credential boundaries:** resolve only the explicitly selected credential source for the approved target; keep custom endpoint/path-style settings configurable and prevent ambient credential fallback. Design separate opt-in live tests, an approved isolated write scope, ownership-based cleanup and fault injection that cannot disrupt shared data or networks. Keep any authorized plaintext exceptions in private test tooling; obtain separate production HTTPS/enterprise-CA evidence. General AWS-profile/SSO product support remains a separate scope decision.

## Risk register

| Risk | Likelihood / impact estimate | Mitigation and trigger |
| --- | --- | --- |
| Existing tools already satisfy the target job | Medium / high product risk | Observe the exact workflow and approval constraints; revise positioning if no meaningful gap |
| Cross-platform UI/accessibility mismatch | Medium / high | Windows/Fedora tests at 1A; block unsupported accessibility claims |
| Package/memory exceeds “lightweight” expectations | Medium / medium | Measure explicit budgets; optimize only with evidence |
| SDK checksum/trailer changes break RGW | High / high | Pin versions, real compatibility suite, scoped settings and verified integrity fallback |
| Unsafe filename mapping or overwrite | Medium / critical | Adversarial platform tests, safe handles, preconditions and fail-closed paths |
| Uncertain writes/partial batches reported as success | Medium / critical | Durable state machine, reconciliation and precise UI outcomes |
| Lost multipart IDs/failed abort cause storage leaks | Medium / high | Journal, cleanup view, lifecycle backstop; disclose unavoidable initiation gap |
| Static keys excluded by target organizations | Medium / high | Manual session support proposal; establish SSO requirement before launch scope freeze |
| No representative Ceph/AWS/Windows tests | High until qualified / high | Application tests, authorized Ceph/AWS targets and Windows VM evidence required; no emulator substitution |
| Administrative reads expose secrets | Medium / critical | Treat raw admin responses as secrets; opt-in display and redaction tests |
| API documentation differs from actual Ceph build | High / high | Versioned contracts, source evidence and integration tests; classify uncertainty |
| Dependency/runtime/signing maintenance burden | Medium / high | Small dependency set, servicing process, provenance and named maintainer |
| Full-object retransmission becomes burdensome | Unknown / high | ADR-006 approved for typical under-1-GB files; revisit for large objects, frequent WAN interruptions, unacceptable retransmission costs or repeated complaints |
| Future administration expands MVP | High / high | Separate phases/contracts; no empty plugin framework or admin placeholders |

These are qualitative planning estimates, not incident frequencies or measured probabilities. Review them at every phase gate.

## Approval record

2026-10-21: user selected architecture approach B. Detailed planning documents remain DRAFT. No implementation, package installation, provisioning, deployment, or production connection is authorized. Subsequent approvals should name the decision IDs, revisions, remaining questions and any scope change. Preserve unresolved questions rather than marking them answered by silence.

2026-10-21, subsequent inventory update: user approved ADR-006 for MVP scope, including durable uncertain outcomes, bounded safe retries, explicit confirmation and whole-object restart after reconciliation; cross-restart partial-byte resume is deferred to Phase 2. Squid (19.x), typical files under 1 GB and LAN/WAN/VPN are confirmed planning inputs. Q02 is resolved for scope only. SQLite remains preferred pending engineering review; the Windows VM remains a 1A provisioning action. No engineering implementation or test evidence is implied by this approval.

2026-10-21, documentation publication boundary: public planning retains architecture B, approved ADR-006, SDK v4/custom-endpoint/path-style requirements and explicit live-test opt-in. Environment-specific configuration, credential-source identifiers, local access approvals and raw execution evidence are kept under ignored `docs/internal/`. Shared manual data must not be overwritten or deleted; destructive operations and dedicated integration-test resource provisioning require approval. Production TLS validation remains mandatory. This documentation update does not authorize implementation, commits or publication.

2026-10-21, final engineering review: architecture B and ADR-006 remain unchanged. The latest user request brings explicit profiles and read-only S3 browsing into 1A; writes and recovery remain 1B/1C. Exact dependency candidates and conditional SQLite recommendations are documented in [PHASE1A_PLAN](PHASE1A_PLAN.md). These recommendations are not new user approvals or executed tests. Coding approval is pending; commits, pushes and provisioning remain unauthorized.

2026-10-21, Session 1 approval and execution: the user accepted the engineering GO and explicitly approved T01–T03 only, including necessary toolchain/package installation. Four production projects, two offline test projects and the Avalonia shell are implemented; 15 tests pass. See [Session 1 evidence](SESSION1_IMPLEMENTATION.md). Later sessions and commits/pushes remain unapproved. No provider, vault or Windows qualification is implied.

2026-10-21, Session 2 approval and execution: the user explicitly approved T04–T05. Profile metadata, explicit target binding, session-only credentials, Windows/Linux vault adapters and the editor are implemented for review. See [Session 2 evidence](SESSION2_IMPLEMENTATION.md) for offline results, security tradeoffs and blocked native checks. No ADR or module boundary is reopened; Session 3 and commits/pushes remain unapproved.

2026-10-22, Session 3 approval and execution: the user accepted Session 2 with its existing limitations and explicitly approved T06 only. Provider-neutral read-only sessions, explicit AWS SDK v4 clients, bounded requests and protocol fixtures are implemented for review. [Session 3 evidence](SESSION3_IMPLEMENTATION.md) records 134 passing offline tests and one skipped native-vault test. Architecture B and ADR-006 remain unchanged. Graphical browsing, live S3 access, writes, commits and pushes remain outside this approval.

2026-10-22, Session 4 approval and execution: the user accepted Sessions 1–3 and explicitly approved T07–T08. The read-only remote browser, exact-key display separation, bounded paging, cancellation/generation protection and regression tests are implemented for review. [Session 4 evidence](SESSION4_IMPLEMENTATION.md) records 168 passing offline tests and one skipped native-vault test. Architecture B and ADR-006 are unchanged. Live qualification, transfers, SQLite, administration, Windows packaging, commits and pushes remain outside this approval.

2026-10-22, Session 5 approval and execution: the user accepted Sessions 1–4 and approved T09–T10. Updated authority permits controlled opt-in read/write S3 experiments only in the disposable laboratory; shared manual data is preserved by default. Cluster/OSD/RGW/WireGuard/VM teardown requires separate final approval. The application and qualification harness remain read-only; separate manifest-owned fixture creation/cleanup supplied pagination and exact-key cases. [Session 5 evidence](SESSION5_IMPLEMENTATION.md) records SDK reads, Linux publishing and pending native gates. Architecture B and ADR-006 remain unchanged; no transfer/recovery implementation, commits, pushes or Session 6 is authorized.

2026-10-22, Session 6 approval and execution: the user accepted Sessions 1–5 and approved T11–T12, keeping the application read-only. Fedora Windows x64 cross-publishing, ZIP notices/provenance and offline interop checks are authorized; the user confirmed the Windows 11 VM is not available. Native Windows build/runtime/vault/accessibility/performance gates remain BLOCKED. [Session 6 evidence](SESSION6_IMPLEMENTATION.md) records construction and regression results. Architecture B and ADR-006 are unchanged. Phase 1B, provisioning, commits, pushes and laboratory teardown remain unapproved.

## Phase 1B.2 authorization

The user explicitly authorized single-file uploads/downloads as a pre-alpha increment, deferring ADR-006 durability to Phase 1C without changing its MVP contract. No overwrite fallback, multipart, SQLite, persistent queue, restart replay or resume is authorized. The user subsequently approved removing the end-user qualification-file gate: ordinary uploads attempt conditional creation with explicit profile credentials, while laboratory opt-in, independent target/scope approval and ownership manifests remain test-only requirements. ADR-008 continues to prohibit unconditional fallback; 412 collisions and explicit unsupported-request/permission failures stop the upload. Remote no-overwrite protection depends on provider enforcement and is not a universal compatibility guarantee. Downloads retain native no-replace commit.
