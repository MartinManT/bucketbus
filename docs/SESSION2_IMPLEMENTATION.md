# Phase 1A Session 2 implementation evidence

Status: T04–T05 implemented for review on 2026-10-21. Session 2 was explicitly approved. Architecture B, ADR-006 and the four production-module boundaries remain unchanged. No Session 3 work, S3 requests, AWS CLI credential access, commits, pushes or infrastructure provisioning were performed.

## Delivered behavior and boundaries

- **Core:** immutable profile records and revisions, HTTPS endpoint validation, exact Unicode prefix preservation, opaque target-bound credential references, explicit credential outcomes, an in-memory session store and create/edit/delete workflows. No external framework/provider packages.
- **Platform:** schema-1 JSON profiles under the current user's local application-data `BucketBus` directory; Windows Credential Manager and Linux Freedesktop Secret Service adapters. Only Platform contains native calls or D-Bus types.
- **Desktop:** constructor-injected profile editor with create/edit/delete, storage selection, masked credential inputs, credential availability checks, explicit unlock permission, cancellation and cleanup retry. No saved secret is populated into the editor. Views handle layout/focus only.
- **S3:** unchanged; no connection/client/browsing implementation.

The JSON schema contains profile identity, profile/document revisions, settings and an optional opaque local credential reference. It never contains access keys, secret keys or session tokens. It is local configuration, **not an import/export interchange format**. Missing required fields, unknown fields, duplicate properties, malformed data, invalid bindings, excessive sizes and unknown schema versions fail closed. There is no historical schema to migrate yet; a future schema requires an explicit migration rather than replacement.

Profile saves take an exclusive file lock, check the expected document revision, write a uniquely named same-directory temporary file, flush it to disk and atomically rename it. Cancellation is checked before the commit point; successful rename is reported as committed. The original file survives injected failures before rename. Linux directory/file modes are 0700/0600; Windows uses the current user's application-data permissions. Profile/lock links are refused. Lock contention reports failure rather than silently overwriting a competing writer. In-process and cross-instance stale edits are rejected by revisions. This is local-filesystem atomicity evidence, not a power-loss guarantee; directory-entry durability, unusual/network filesystems and Windows replacement semantics still need native qualification.

## Security-sensitive choices

1. Endpoints accept HTTPS host plus optional port only. User information, raw whitespace/control characters, backslashes, query/fragment forms, percent escapes and endpoint paths are rejected before URI normalization. There is no shipped HTTP or TLS-validation bypass. Prefix strings are not normalized, decoded or trimmed; malformed UTF-16 and prefixes exceeding 1,024 UTF-8 bytes are rejected.
2. Credential references include independent profile/binding IDs and a fingerprint of the canonical endpoint, signing region and addressing mode. Changing any of these detaches the old binding unless replacement credentials are explicitly supplied. A reference cannot resolve across profile/target boundaries. Rename or bucket/prefix edits retain a matching binding.
3. Vault replacement uses a fresh reference. A failed metadata save cannot replace the previous bound secret. Deletion commits profile removal before vault cleanup. Failed/uncertain vault writes are cleaned up without opening another unlock prompt; unresolved cleanup is reported and can be explicitly retried. No automatic fallback to session storage or plaintext occurs.
4. Vault and JSON updates are **not one transaction**. A crash between them, or exit with failed cleanup, can leave an unbound application-owned vault entry. Cleanup tracking currently lasts for the running process; the UI warns to retry before closing and that manual OS-vault removal may be needed. Such entries are never rebound by name or automatically used. Do not claim durable credential-cleanup recovery. ADR-006 transfer cleanup/journaling remains future work.
5. Windows uses generic current-user credentials with local-machine persistence (not enterprise roaming), opaque targets and a fixed non-secret username. Blob byte limits are enforced without truncation. Read buffers are released with `CredFree`; owned native and managed byte buffers are cleared where feasible. [Microsoft Credential Manager API](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credreadw), [credential structure](https://learn.microsoft.com/en-us/windows/win32/api/wincred/ns-wincred-credentialw).
6. Linux uses Tmds.DBus **0.95.1**, a verified MIT dependency with no additional net6.0 dependency requirements. It targets the established Secret Service methods, uses an existing default collection and never creates a keyring. Missing services/collections, locks, denied access, dismissed prompts and operation failures remain distinct. Prompts subscribe before display, dismiss on cancellation and unsubscribe afterward. Operations have a 60-second total bound plus at most one second each for prompt dismissal/session close. Connection/session ownership is per operation.
7. Secret Service `plain` sessions are restricted to an authenticated **local Unix session bus**, rejecting TCP addresses and fallback address lists. This uses OS-local IPC protection; it does not add application-layer D-Bus encryption or isolate secrets from compromised same-user processes. Secret Service persistence/lock protection remains the OS service's responsibility. No subprocesses or secret-bearing command arguments are used. [Secret Service transport](https://specifications.freedesktop.org/secret-service-spec/latest/ch07s02.html), [Tmds type mapping](https://github.com/tmds/Tmds.DBus/blob/main/docs/modelling.md).
8. Temporary session-token credentials are always memory-only. Session credentials disappear at exit, and their persisted opaque references resolve as Missing afterward. Native adapters independently reject token-bearing writes. Owned byte buffers are cleared; managed strings and library-internal copies cannot be guaranteed zeroized. Neither raw native exceptions nor credential values are logged or presented.

## Validation

Fedora x64, .NET SDK **10.0.113**, runtime **10.0.13**. Existing exact package versions are retained; only `Tmds.DBus [0.95.1]` is added directly to Platform. Avalonia's separate transitive `Tmds.DBus.Protocol 0.94.1` remains unchanged. Seven project lockfiles are preserved, covering 60 package/version pairs. NuGet's dated transitive audit reports no known vulnerabilities. See [dependency inventory](DEPENDENCIES.md).

`./eng/validate.sh` completed successfully with the selected SDK and telemetry opt-outs. Offline coverage includes all 15 original Session 1 tests, profile/schema/binding invariants, interrupted writes, concurrency conflicts, session disposal, failed vault/metadata writes, failed cleanup, native error classification, Windows marshaling/buffer cleanup against a fake API, real Tmds proxy generation without connecting, Secret Service prompt/session behavior against fakes, and headless editor focus/loading/errors/secrets handling.

| Check | Result / evidence limit |
| --- | --- |
| Locked restore | **PASS**, all seven projects |
| Release build | **PASS**, zero warnings / zero errors |
| Offline tests | **82 passed**, zero failed: Core 34, Platform 38, Desktop 10; all original 15 retained |
| Opted-in native test in default run | **1 skipped**, before vault access; not native pass evidence |
| Linux Secret Service native lifecycle | **BLOCKED**: no interactive graphical desktop session; no real vault was accessed |
| Windows native Credential Manager / portable runtime | **BLOCKED**: Windows 11 VM remains unavailable |
| Actual desktop/editor operation | **BLOCKED**: no interactive display; headless tests are separate evidence |
| Live Ceph / AWS | Not run; outside Session 2 |

The native vault lifecycle test is tagged `Category=NativeVault`, skips before adapter construction unless explicitly opted in, and additionally requires an interactive Linux desktop. On a suitable authorized user session, use:

```bash
BUCKETBUS_RUN_NATIVE_VAULT_TESTS=1 dotnet test tests/BucketBus.Platform.Tests \
  -c Release --no-build --no-restore --filter Category=NativeVault
```

On Windows, set the same environment variable for that test process. Keep the repository's documented telemetry opt-outs. The test generates only a disposable application-owned credential, never enumerates existing entries, and attempts deletion in `finally` even after cancellation/failure. An opted-in failure is a failure, not proof of native compatibility. A cleanup failure requires inspection of BucketBus-owned test entries in the OS vault. Interactive locked/unlocked/cancelled/denied scenarios still need native manual evidence beyond this simple round-trip.

## Prerequisites for Session 3

- User approval for T06; do not start automatically.
- Keep the explicit profile revision/target and credential-reference contracts when constructing S3 sessions. Do not introduce the SDK ambient credential chain, retarget an existing client, or forward credentials across redirects.
- Implement offline SDK wire fixtures for path-style/custom endpoints, exact keys, nullable responses, pagination, direct-bucket permissions, cancellation and bounded retries before provider qualification.
- Production profiles continue to require HTTPS. Any later authorized HTTP laboratory harness stays test-only and outside the shipped composition; no live laboratory use is authorized by this session.
- Native Linux desktop/vault and Windows VM checks remain open Phase 1A exit gates. They do not prevent offline Session 3 development but must not be reported as passed.

Stop here for review. Browsing, transfers, SQLite journals and Ceph administration remain unimplemented.
