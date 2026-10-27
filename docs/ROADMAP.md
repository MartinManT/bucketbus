# BucketBus roadmap

Status: DRAFT gated roadmap · 2026-10-21

This is a sequence of reviewable outcomes, not a delivery-date promise. Typical files under 1 GB, LAN/WAN/VPN networks, Squid (19.x) as the primary initial Ceph validation family, and Fedora/.NET 10 SDK/Docker development are confirmed planning inputs. Maximum file size, object counts, interruption frequency, qualified provider configurations, isolated write-test authority, staffing, available hours, AWS budget and signing resources still require resolution. Windows 11 VM evaluation is a Phase 1A prerequisite. Local infrastructure readiness and run records are maintained separately; calendar estimates before resolving delivery inputs would imply unjustified precision.

## Phase 0: discovery and architecture

Deliver the nine planning documents, distinguish confirmed user requirements, approved decisions, proposed engineering choices, unresolved validation requirements and executed evidence, and review scope/UX/engineering. The small modular application shape is confirmed and ADR-006's recovery scope is approved. SQLite was preferred at discovery; explicit Phase 1C.1 approval and implementation evidence are recorded below. Q02 is resolved for scope; its remaining workload measurements do not reopen that approval by themselves. Session 1 now has [offline foundation/shell evidence](SESSION1_IMPLEMENTATION.md); no SDK provider qualification has been performed. Local connectivity checks cannot satisfy application qualification gates.

Exit: approve or revise the detailed baseline, assign unresolved questions, and explicitly authorize implementation. The [engineering review and implementation plan](PHASE1A_PLAN.md) is complete with conditional GO to begin 1A after coding approval. No new product discovery or mandatory additional planning cycle is recommended. Independent outside engineering coverage was unavailable. Do not treat a completed review as permission to scaffold.

## Phase 1: reliable file management

| Milestone | Outcome | Exit evidence |
| --- | --- | --- |
| 1A: read-only platform foundation | Shared desktop shell, explicit profiles, secure credential adapters, read-only bucket/object/metadata browsing, offline and opted-in integration tests, self-contained packaging; Windows VM when separately authorized | T01–T12 in the implementation plan; Windows clean-VM and Fedora desktop/vault checks; size/memory/startup measurements. VM is not available yet |
| 1B: first complete workflow | Extend 1A browsing with local pane and single-file upload/download; precise errors and safe overwrite/integrity behavior; record selected Squid package/build provenance and configuration; approve isolated write fixtures | Real restricted-bucket workflow on identified Squid and AWS, not only a mock; document LAN/WAN/VPN path and TLS trust |
| 1C: transfer correctness | Approved ADR-006 recovery, bounded queue/retries, multipart, integrity/conflict protection, safe paths, cancellation and durable uncertain outcomes/cleanup | Fault/crash/adversarial and impaired-network tests; confirmation/reconciliation gates; no silent replay or false-success cases |
| 1D: everyday usability | Selected directory/prefix copy, metadata, exact-object deletion, keyboard/drag/drop, themes and accessible recovery | Formative usability exercise, Windows DPI/input tests and Linux integration |
| 1E: public v1.0 qualification | Release artifacts and honest compatibility claims | Required PRD tests, actual Ceph/AWS matrix, signed Windows ZIP, SBOM/provenance/checksums |

Milestone descriptions are implementation planning only. They do not authorize work now. Test scaffolding and UI feasibility code begin only after the hard stop is lifted.

Do not postpone AWS tests until 1E: catch SDK assumptions early at 1B. Likewise, Windows execution begins at 1A despite Linux being the development host. Linux must remain buildable and exercised throughout; claim distributable support only for tested OS/desktop configurations.

### First internal alpha

After implementation authorization, an early development alpha may use explicitly opted-in read-only checks against an authorized Squid target and Fedora/Docker fixtures for controlled faults. Real upload/download and overwrite qualification requires approved isolated fixtures; shared manual data must not be overwritten or deleted. AWS validation is required at 1B. CLI reads alone do not establish application compatibility. Use typical sub-1-GB examples plus multipart-boundary cases, with impaired-network/failure fixtures early. Exercise a Windows 11 VM at 1A before claiming Windows portability. Any alpha exposing restart recovery must retain uncertainty, require explicit confirmation and avoid silent replay; full recovery qualification remains 1C. Cross-restart partial-byte resume is not an alpha/MVP gate. Build-specific S3 tests and larger stress cases remain required at their milestones.

## Phase 2: advanced S3 features

Choose increments based on observed usage, not a commitment to deliver the whole list: cross-restart partial resume, AWS profiles and credential refresh/STS/SSO, S3-to-S3 copy, version browsing/restore, tags, presigned URLs, storage-class/encryption selection, bandwidth controls, and bucket policy/versioning/lifecycle/ACL editing.

Cross-restart partial-byte resume is explicitly deferred here by approved ADR-006. Revisit its priority during alpha feedback and before 1E if larger objects, frequent WAN interruptions, unacceptable retransmission costs or repeated user complaints appear. Retain approved integrity, overwrite and reconciliation safeguards in any revised design. Limited-bandwidth testing is required in Phase 1 even though a user-configurable bandwidth limiter remains a Phase 2 feature.

Standard S3 management belongs here even on a Ceph endpoint. Every privileged editor requires preview, conflict handling, explicit confirmation and read-back. Remote rename must expose copy/delete semantics. Synchronization needs its own comparison rules, dry-run plan, deletion safeguards and failure model; it does not follow automatically from directory copying.

Exit per increment: provider-specific acceptance, security review of new authority/data paths, compatibility evidence and UX review. Credential acquisition may move earlier if required by the actual launch environment; record that scope change before implementation.

## Phase 3: optional Ceph administration

Start with a separate read-only workspace for documented user/bucket information and statistics. Add legacy RGW user management, key rotation, quota and capability changes in small increments. Add account IAM only for explicitly qualified releases and configurations. Separate Admin Ops from IAM authority in both code and interface.

Prerequisites: actual builds and test authority, capability matrix, secret-bearing response redaction, audited change workflows, and two relevant release/build test targets for the claimed identity models. Scope out SSH, direct cluster metadata manipulation, complete effective-permissions calculation and unproven ownership migration.

Exit: ordinary transfers still work without admin credentials; admin denial cannot break the standard workspace; sensitive reads and writes pass their own security and integration suites. Do not delay a sound Phase 1 release waiting for this phase.

## Phase 4: demand-driven enterprise extensions

Potential work: organization identity integration beyond Phase 2 needs, centrally governed settings, opt-in administrative audit export, separate Dashboard API adapter for read-only multisite visibility, and carefully justified deployment integrations. A portable encrypted vault is optional and needs an independent key/recovery model.

**Roadmap correction:** no telemetry by default, OS-protected credentials, strict TLS, dependency transparency, safe destructive actions, accessibility fundamentals and release provenance start in Phase 1. They are not premium enterprise extras. Phase 4 is not a promise to become a full cluster administration console.

## Scope-change and pause rules

For a new feature, state the user task it unlocks, the phase it displaces, the new permission/provider contracts, and acceptance evidence. Add an ADR-style decision rather than expanding a generic interface preemptively.

Reassess the project if observed users complete the exact target tasks satisfactorily with an approved existing tool, if the framework cannot meet core accessibility/packaging gates, or if no real Ceph/AWS validation can be obtained. These conditions call for an explicit product decision, not claims of success from an emulator.

## Immediate implementation handoff

Sessions 1–6 are approved; see [Session 6 results](SESSION6_IMPLEMENTATION.md) for the latest validation and remaining native gates. Windows VM readiness gates 1A completion, not Fedora artifact construction. SQLite mechanisms, application writes and recovery remain gated at their implementation milestones. Remaining questions and deadlines are in [DECISIONS](DECISIONS.md#open-questions-and-defaults). No Phase 1B, provisioning, commit or push is authorized by this handoff.

## Session 6 gate update

T11 has a Fedora-produced Windows x64 evaluation ZIP with locked publishing, notices and measured distribution sizes. Actual Windows build/runtime evidence and T12 remain BLOCKED: the user confirms the VM is unavailable. [Session 6 evidence](SESSION6_IMPLEMENTATION.md) and the [Windows checklist](WINDOWS_VALIDATION.md) define the remaining handoff. Phase 1A cannot close until native Windows and remaining Fedora/vault/accessibility/performance gates are resolved. No Phase 1B or infrastructure provisioning is authorized.

## Phase 1B.2 execution

Single-file create-only uploads and safe downloads are implemented for review, with one active transfer, progress/cancellation and explicit ambiguous outcomes. Identified Ceph qualification covers small/zero-byte/Unicode round trips and collision protection; this does not qualify all Squid deployments or AWS. This remains pre-alpha. Phase 1C durability, native Windows transfer qualification, larger WAN workloads and later transfer features remain outstanding. No later phase starts automatically.

## Phase 1C.1 execution

SQLite selection is now explicitly approved. Durable single-file history and conservative,
explicit recovery are implemented; see [results and limitations](PHASE1C1_IMPLEMENTATION.md).
No automatic replay, batch queue, retry scheduler or multipart support is included.
Phase 1C.2 queue/retry work requires a separate authorization. Windows native execution,
Fedora GUI acceptance of the new recovery panel, and power-loss qualification remain open.
No live provider tests or infrastructure changes were performed in this increment.

## Phase 1C.2a execution

The selected-file sequential queue is explicitly authorized and implemented with
schema-2 migration, one active transfer, explicit Start/Continue, pause-after-current,
stop-on-error and removal of never-started jobs. See [results](PHASE1C2A_IMPLEMENTATION.md).
Phase 1C.2b safe bounded retries/network impairment work remains separately gated;
no multipart, recursive transfers, background replay or later increment is started.
Native Windows and Fedora GUI qualification remain distinct from offline evidence.

## Phase 1C.2b execution

The user explicitly approved bounded download resilience. Three total whole-file
GET attempts, typed eligibility, cancellable jittered backoff, native retry gates
and schema-2 payload evidence are implemented and tested. PUT retry, multipart,
partial resume, parallel scheduling and later increments remain outside scope.
[Executed results and remaining native limits](PHASE1C2B_IMPLEMENTATION.md) apply;
no live provider or infrastructure disruption was performed.

## Phase 1C.3 execution

Sequential streaming multipart uploads and explicit safe resume/abort extend the
existing queue. Creation, part, completion and cleanup uncertainty are durable;
startup never accesses credentials or S3. [Implementation and qualification limits](PHASE1C3_IMPLEMENTATION.md)
separate offline evidence, packaging and unexecuted native/live gates. Fresh live
endpoint and isolated write-scope authorization is required for multipart qualification.
No later increment, infrastructure changes or automatic replay is started.
