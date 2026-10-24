# Session 6 implementation evidence — T11–T12

Historical evidence: the artifact checksum below identifies the original Session 6
build. The 2026-10-23 [pre-commit remediation](PRE_COMMIT_REMEDIATION.md) rebuilt the
same local output path; use its newer checksum for the current evaluation ZIP.

Executed 2026-10-22 on Fedora 44 x64. The user accepted Sessions 1–5 and approved
Windows packaging/native qualification, while preserving read-only application
scope. The user explicitly confirmed the Windows 11 VM is **not available yet**.

**T11 artifact construction PASSED on Fedora; required native Windows build checks
remain BLOCKED. T12 is BLOCKED. Phase 1A completion remains NO-GO.** The artifact
is ready for internal Windows evaluation when an authorized VM is available.
No production code, package version, architecture boundary or ADR was changed.

## Delivered workflow and artifact

- `eng/publish-windows.sh`: exact SDK check, telemetry opt-outs, dedicated RID
  locked restore, Release self-contained multi-file publish with single-file,
  trimming and AOT explicitly disabled. Fresh temporary staging is cleaned on exit.
- `eng/package-windows.py`: validates required x64 PE components and runtime,
  excludes managed/native symbols and unexpected configuration, copies selected
  public documentation and dependency notices, and creates a sorted ZIP with fixed
  entry metadata. Validates ZIP integrity before replacing the prior ZIP.
- Four `src/*/packages.win-x64.lock.json` files. All nine ordinary and four Linux
  lockfiles remain byte-for-byte unchanged. Windows/Linux graph unions have the
  same 36 resolved package/version pairs; ordinary graphs remain at 60.
- Six additional offline Windows interop regression cases: five error categories
  across read/write/delete, and x64 native structure size/offset verification.
- [Windows checklist](WINDOWS_VALIDATION.md), portable artifact README, license
  inventory and [supplemental upstream notices](third-party/README.md).

Construction output is local and ignored:
`artifacts/session6/BucketBus-win-x64.zip`, `.zip.sha256`, and
`BucketBus-win-x64.manifest.json`. Copy the ZIP/checksum together to the future VM.

| Final artifact measurement | Result |
| --- | --- |
| ZIP SHA-256 | `79593499a7dfb4596fb8a91645ce3dc12aa9c5cb1386cdc22430edbd54c6913f` |
| ZIP bytes | 48,239,347 (46.005 MiB) |
| Sum of expanded file bytes | 113,716,196 (108.448 MiB); not filesystem allocated size |
| Files | 277, including documentation and notices |
| Proposed N05 budgets | PASS for construction: ≤150 MiB ZIP / ≤350 MiB expanded |
| Repeated publishing | Two complete workflow runs produced identical ZIP SHA-256, byte sizes and file count in this environment |

This is an **unsigned internal evaluation artifact**, not a public release or a
Windows compatibility claim. Repeatability was measured on the same Fedora
checkout/toolchain; independent clean-machine reproducibility remains untested.
The ZIP toolchain was Python 3.14.8 with zlib `1.3.1.zlib-ng`.
The manifest records production source and Windows lockfile SHA-256 values because
the implementation is still uncommitted. A complete release SBOM, reviewed source
revision, signing and final exact-source notice verification remain release work.

The ZIP includes .NET runtime 10.0.13 (`coreclr.dll`, `hostfxr.dll`, `hostpolicy.dll`)
and Windows native SkiaSharp, HarfBuzz and ANGLE. These and the application host
have verified x64 PE headers. Runtime configuration lists the included framework;
the apphost manifest requests `asInvoker`. These are static construction checks,
not proof of successful Windows execution, elevation behavior or graphics loading.

The packager copies available license/notice texts from restored packages and the
runtime pack. The mapped notice inventory includes 36 packages plus the runtime,
conservatively retaining some build/non-Windows dependency information. AWS notice
texts are a dated upstream snapshot because package metadata supplies no exact
source revision. That limitation is explicit in the bundled notice provenance.
No PDBs, tests, AWS configuration, profile JSON, credentials or private evidence
are packaged. Manifests contain relative source paths, not local absolute paths.

## Executed validation

| Check | Actual result and limit |
| --- | --- |
| Exact SDK / runtime | 10.0.113 / bundled 10.0.13; no SDK/system upgrade |
| Exact application dependencies | Avalonia 12.1.4; AWSSDK.S3 4.0.104.5 / Core 4.0.102.12; MVVM 8.4.2; Microsoft extensions 10.0.13; Tmds.DBus 0.95.1; unchanged central pins |
| Test toolchain | xUnit v3 3.2.2, VS adapter 3.1.5, Test SDK 18.11.0; unchanged |
| Main locked restore / Release build | PASSED, zero warnings/errors |
| Core offline tests | 34 passed |
| Platform offline tests | 44 passed, one native-vault test skipped |
| Desktop offline/headless tests | 44 passed |
| S3 offline/SDK regression tests | 52 passed, retaining leading-slash coverage |
| Ordinary suite total | **174 passed**: all 168 existing tests plus six new interop cases; native vault BLOCKED/skipped |
| Integration locked restore / Release build | PASSED, zero warnings/errors |
| Integration guards | **14 passed**, live category excluded; no live access |
| NuGet audits | Ordinary solution, integration solution and Windows Desktop graph: no known vulnerable packages reported, including transitives, on 2026-10-22; a dated feed result, not a security guarantee |
| Windows locked publish | PASSED; repeated locked workflow preserves ordinary/Linux lock hashes |
| Packaging validation | Bash/Python syntax checks, ZIP CRC integrity, required native PE/runtime checks and packaged-content inspection passed |
| Packaging failure injection | Synthetic `profiles.json` and corrupt runtime each rejected; previously completed ZIP hash remained unchanged |
| Final ordinary/integration restore | Locked restore repeated after publishing, restoring normal assets without lockfile changes |
| Actual Windows execution / native vault / DPI / accessibility / timing / memory | **BLOCKED**, VM unavailable; no Wine or headless substitute reported as Windows evidence |
| Linux native requalification / live S3 | Not run in Session 6; Session 5/user evidence retains its original limits |

An early diagnostic publish was invoked before the initial Windows restore had
finished and failed NETSDK1047 (RID target absent). After restore completed, publish
passed. The delivered script awaits successful restore before publish. No default
lockfile regeneration or dependency change was needed. Detailed build/audit logs
and local failure-injection evidence remain under ignored `docs/internal/`.

## Credential Manager review

The existing adapter retains Unicode `CredReadW`/`CredWriteW`/`CredDeleteW`, Win32
BOOL marshalling and captured native error codes. The sequential x64 CREDENTIAL
layout is 80 bytes with checked blob/persistence/user-name offsets. It uses generic
credentials and `CRED_PERSIST_LOCAL_MACHINE`: persistence for the same user on the
same computer, not shared access by every local user. See Microsoft's
[CREDENTIALW contract](https://learn.microsoft.com/en-us/windows/win32/api/wincred/ns-wincred-credentialw).

Reads release the returned allocation with `CredFree`; owned buffers are cleared
where possible, the 2,560-byte blob limit is enforced without truncation, and
temporary session tokens are rejected for secure persistence. The
[CredRead contract](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credreadw)
requires freeing the single returned allocation. Offline fakes cover cleanup and
malformed data; the new cases cover Missing, Denied, Unavailable, Cancelled and
retrieval/storage failures without exposing secret values.

These findings validate the inspected implementation and managed test seams.
They **do not prove native interoperability**, store policy, restart persistence or
native denied/failure behavior. Windows Generic Credentials do not promise a
Linux-style unlock prompt. The existing opted-in native test uses a generated
application-owned entry and attempts deletion in `finally`; the Windows checklist
separates that lifecycle from manual restart/UI/fault checks. Session 2's possible
crash-orphan vault entries remain a documented limitation; no plaintext fallback
or new persistence mechanism was introduced.

## Repository checkpoint and security boundary

Git still tracks only the original LICENSE; Sessions 1–6 source/configuration/docs
are untracked and nothing is staged. Generated `bin/`, `obj/`, test results,
`artifacts/`, Python cache, `.env*` and `docs/internal/` are ignored. Public source,
documentation, vendored notices and all lockfiles remain eligible for review.
Public-candidate scans found no real infrastructure/credential patterns; two
initial name matches were synthetic temporary-path tests. This targeted scan does
not replace human review before publication.

Recommended checkpoint: review the complete Sessions 1–6 source baseline and
dependency/notice changes, then create one explicitly approved source checkpoint
including all lockfiles and public evidence, excluding local artifacts/configuration.
No commit, push, live S3 request, credential-file access, vault mutation, resource
provisioning or laboratory mutation/teardown occurred in Session 6.

## Phase 1A exit assessment and next handoff

**GO to evaluate the constructed ZIP once an authorized VM exists. NO-GO to declare
Phase 1A complete. No Phase 1B implementation is authorized.**

| Remaining gate | Required before closure |
| --- | --- |
| T11 Windows-native construction | Actual Windows locked restore, Release build and offline/guard tests; compare native publish behavior with the supplied ZIP |
| T12 clean Windows runtime | Standard-user launch without installed .NET/elevation, alternate/non-ASCII/read-only paths and state outside the executable directory |
| Windows credentials | Actual disposable vault lifecycle/cleanup, restart/rebinding, missing and safely reproduced denied/failure paths; no plaintext fallback |
| Native input/accessibility | Windows keyboard/focus, mixed DPI and Narrator/NVDA; remaining Fedora desktop/accessibility checks |
| Performance | Actual startup, private-working-set and responsiveness/page-render samples with budget disposition; package-size construction gate already passes |
| Fedora completion | Self-contained artifact launch without installed runtime and actual Secret Service lifecycle/locked/cancelled/unavailable evidence; existing user smoke test is narrower |
| Qualification limits | Preserve the scoped Ceph read evidence; AWS, restricted identity, network fault and exact Ceph build provenance gaps remain at their documented milestones, before broader claims |

Use [WINDOWS_VALIDATION](WINDOWS_VALIDATION.md) for the precise VM procedure and
[FEDORA_VALIDATION](FEDORA_VALIDATION.md) for remaining laptop checks. Capture
failures and cleanup obligations, do not replace blocked gates with assumed passes.
Architecture B, approved ADR-006, exact keys, explicit credentials and normal HTTPS
validation are unchanged. Work stops at the Session 6 review boundary.
