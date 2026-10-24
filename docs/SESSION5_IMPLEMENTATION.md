# Session 5 implementation evidence — T09–T10

Executed 2026-10-22. The user accepted Sessions 1–4 and approved live S3 qualification and Fedora evaluation. Their updated authorization additionally permits controlled S3 mutations in the disposable Ceph laboratory. Architecture B and ADR-006 remain binding. The application remains read-only. Session 6, Windows packaging and final laboratory infrastructure teardown remain unapproved.

## Delivered implementation

- Separate `BucketBus.S3.IntegrationTests` project and `BucketBus.Integration.slnx`; no live cases in the ordinary solution.
- Explicit opt-in and private configuration gate before credentials/client/network, fixed-schema target selection, exact named literal shared-credentials binding, no ambient fallback, bounded actual SDK reads and a second GET/HEAD-only transport boundary. Only test-assembly friend access was added to S3; no production feature or security policy changed.
- Offline guards for disabled runs, missing configuration, unsafe/mismatched authorities, page bounds, private-path escape, explicit credential selection and mutation rejection. `eng/validate-integration.sh` filters out live cases even when caller opt-in variables are present.
- Reproducible self-contained Linux publishing using separate RID lockfiles and `eng/publish-linux.sh`.
- [Live harness instructions](LIVE_QUALIFICATION.md) and [Fedora evaluation procedure](FEDORA_VALIDATION.md).

Secrets remain in the existing shared-credentials file; neither that file nor AWS CLI configuration was modified. Live configuration, manifests and raw evidence remain ignored under `docs/internal/`. No plaintext endpoint or TLS bypass was introduced. Public results below omit infrastructure and object identifiers.

## Actual Ceph SDK qualification

The target is a user-identified **Ceph RGW Squid 19.2.7** deployment over HTTPS/private VPN, using path-style addressing and an explicitly configured signing region. TLS used normal chain/hostname verification. Exact package/backport/build provenance and broader configuration remain unconfirmed; the test did not administer or inspect the cluster. SDK packages remain AWSSDK.S3 4.0.104.5 / AWSSDK.Core 4.0.102.12.

The same live read case ran successfully twice: first on existing shared manual data, then on a dedicated generated fixture bucket. These are two executions of one integration case with per-operation evidence, not two comprehensive provider certifications.

| Gate | Executed result | Limit |
| --- | --- | --- |
| Explicit direct-bucket listing | PASSED on both targets | No preliminary discovery/HEAD required |
| ListBuckets | PASSED on both runs | Identity can discover; restricted-denial flow remains offline evidence |
| ListObjectsV2 | PASSED | Configured bounded pages, no eager bucket traversal |
| Pagination | PASSED on isolated fixtures, multiple pages consumed to end | Existing shared data alone was insufficient; not a million-object provider benchmark |
| On-demand metadata | PASSED for existing object and seven fixture cases | Only requested fields/keys tested |
| Leading-slash HEAD workaround | PASSED for one/two leading slashes and slash-only keys | Confirms current pinned SDK correction on this deployment; do not remove it or generalize to AWS |
| Unusual exact keys | PASSED metadata cases for NFC/NFD, literal plus/percent and marker keys | Core kept exact requested identity; broad provider key-space parity remains unproven |
| Pre-cancelled operation | PASSED with zero additional sends | Does not qualify actual WAN interruption/backoff cancellation |
| Normal HTTPS verification | PASSED for these connections | No enterprise CA, interception or alternate-certificate compatibility claim |
| Restricted identity / permission denial | NOT TESTED live | No separately selected restricted principal configured |
| Retry exhaustion, timeout, VPN faults | NOT TESTED live; existing deterministic fixtures pass | No artificial network changes made |
| AWS S3 | NOT TESTED | Ceph evidence never establishes AWS compatibility |
| Virtual-hosted addressing | NOT TESTED live | Existing production addressing restrictions remain |

### Authorized mutations and cleanup

The separate private fixture tool created **one uniquely named, manifest-owned bucket**, then **1,008 small objects**: 1,001 flat keys to cross the production 1,000-entry page boundary and seven unusual-key/marker fixtures. All PUT responses were confirmed; no writes were reported failed or uncertain. Tooling used explicit named-profile credentials, normal HTTPS, an authority/method check, no proxies and one attempt per call. These writes used Python botocore fixture tooling; they are not AWS SDK for .NET transfer qualification.

The read-only .NET suite performed GET/HEAD against those fixtures. Cleanup then deleted only the 1,008 manifest-owned keys, verified the bucket empty, deleted that bucket and confirmed its absence with a 404. A local bucket-name length assertion initially stopped cleanup before any cleanup request; after correcting the validation, cleanup completed. The manifest was retained. No ambiguous cleanup outcome remains.

The existing shared bucket and its data were preserved. No cluster/OSD/RGW/WireGuard/VM teardown or administrative mutation occurred. No overwrite, multipart, recovery or payload round-trip experiments were performed; those remain later milestone evidence.

## Build, regressions and dependency evidence

Fedora 44 x64, pinned SDK 10.0.113 and runtime 10.0.13. No dependency versions or package references were added; the test project reuses existing packages. There are now nine ordinary lockfiles and four separate linux-x64 production lockfiles. The ordinary union remains 60 package/version pairs.

The normal locked restore / Release build / test sequence and the separate integration guard sequence are recorded in local evidence. All **168 existing offline tests** remain required and preserved; the integration project adds **14 offline guard cases**. The native-vault test and disabled live case retain their explicit skip behavior. Live executions are reported separately above. NuGet audits of the ordinary and integration solutions report no known vulnerabilities on this date, including transitive dependencies. This is a dated feed result, not a security guarantee.

### Linux publish outcome

Initial locked RID publish failed with **NU1004** because ordinary graphs lacked linux-x64 targets. That attempted restore also left partial RID state in ordinary Desktop/Platform lockfiles. The normal graphs were regenerated with the same exact versions, then locked validation was repeated. Four separate `packages.linux-x64.lock.json` files now support reproducible locked publishing; a hash comparison confirmed the RID script changes none of the four default production lockfiles.

Self-contained, multi-file, untrimmed/non-AOT linux-x64 publish **PASSED**, bundling .NET 10.0.13. The evaluation payload is approximately **104 MiB expanded / 43 MiB gzip archive**; exact byte sizes and SHA-256 are recorded locally. These Linux measurements do not validate Windows ZIP budgets or release notice completeness. License/dependency inventory accompanies the local artifact; a complete redistribution notice/signing/provenance review remains a release prerequisite.

Launching the artifact with DOTNET_ROOT variables removed reached the application's fixed `BB-STARTUP` handler and exited 1. No display session is available. This demonstrates bundled-runtime startup into application code; it is **BLOCKED native GUI validation**, not a successful window launch or proof of the exact suppressed startup cause. The no-installed-runtime GUI gate, responsiveness, accessibility and actual startup/idle-memory budgets remain pending on an appropriate desktop. See [Fedora checklist](FEDORA_VALIDATION.md).

## User-provided evidence versus executed checks

The user supplied screenshots and reported a successful **framework-dependent** Fedora/Openbox launch and HTTPS RGW browser connection, showing an existing 27-byte object. This is accepted manual smoke-test evidence. It does not demonstrate self-contained launch without installed .NET, native Secret Service lifecycle, full keyboard/accessibility coverage, permission failures or measured performance.

This headless environment has neither DISPLAY nor WAYLAND_DISPLAY. Native Linux vault and graphical tests remain BLOCKED here; Windows validation remains pending Session 6. No screenshots were generated or substituted with headless captures.

## Remaining gates and Session 6 prerequisites

- Obtain actual laptop results using the supplied artifact/checklist, including native vault success/locked/cancelled/unavailable cases and cleanup, keyboard/accessibility and timing/memory samples.
- Provide an authorized Windows 11 environment and separately approve T11–T12 before Windows packaging/runtime/vault qualification.
- Resolve AWS test access, exact Ceph build/configuration provenance, restricted identity scope and remaining network/enterprise trust requirements before broader compatibility claims.
- Retain the working laboratory for future development. Final teardown requires its own approval after all tests/evidence and dependencies are finished.
- Preserve earlier limits: Session 2 crash-orphan vault entries, SDK leading-slash workaround and virtual-hosted bucket restrictions, finite traversal cap and unvalidated write/recovery semantics.

No commits or pushes were performed. Work stops at T09–T10 for review; Phase 1A as a whole remains open while platform exit gates are incomplete.
