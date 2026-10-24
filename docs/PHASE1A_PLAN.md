# BucketBus engineering review and Phase 1A implementation plan

Status: AUTHORITATIVE implementation handoff · reviewed 2026-10-21 · Sessions 1–6 (T01–T12) approved; Windows cross-publish prepared; native Windows gates BLOCKED (VM unavailable)

## Assessment and scope

**GO accepted by the user; Sessions 1–6 (T01–T12) explicitly approved.** See [Session 1 results](SESSION1_IMPLEMENTATION.md) for executed evidence. See [Session 2 results](SESSION2_IMPLEMENTATION.md) for profile/credential implementation and native-validation limits. The user accepted Session 2 with its documented limitations. See [Session 3 results](SESSION3_IMPLEMENTATION.md) for the read-only backend and actual SDK fixture evidence. Sessions 1–3 are accepted for continued development. See [Session 4 results](SESSION4_IMPLEMENTATION.md) for the browser and regression evidence. Session 5 live reads, controlled laboratory fixture writes and Linux publishing are recorded in [Session 5 evidence](SESSION5_IMPLEMENTATION.md); remaining desktop/vault checks are pending. Session 6 is approved; [packaging evidence](SESSION6_IMPLEMENTATION.md) and the [Windows checklist](WINDOWS_VALIDATION.md) distinguish cross-publishing from native qualification. The user confirms the Windows VM is not available. No architectural blocker requires reopening architecture B or ADR-006. **NO-GO to declare Phase 1A complete** until actual Windows 11 and Fedora desktop checks pass or failed gates receive an explicit disposition. Cross-compilation, headless tests and CLI connectivity are insufficient substitutes.

At the engineering review, the repository had no application or test projects. Package metadata and official API documentation were examined; no restore, compilation, application test or live RGW request was performed for that review. The subsequent approved Session 1 created the foundation/shell and passed 15 offline tests; this does not supply provider or native desktop evidence. Local CLI evidence is separate from future SDK qualification. The gstack outside-review step was unavailable because Claude Code was not installed and the prescribed native fallback capabilities were unavailable; independent outside coverage is missing.

The user's latest request brings connection profiles and read-only bucket/object browsing into **1A**, alongside platform feasibility. **1B** retains the first complete upload/download workflow and real AWS/Ceph write qualification; **1C** retains durable transfer recovery and multipart fault qualification. Keep four production modules, one process, explicit authorities and no plugin platform. Ceph administration, advanced IAM, cross-restart partial-byte resume, AOT and trimming remain deferred.

Existing requirements and approved decisions remain binding. The T01–T03 package set and shell foundation were subsequently approved and validated in Session 1. Future interfaces, timeout values and implementation details below remain **engineering recommendations pending their authorized sessions and validation**. SQLite remains preferred: this review supports that choice with conditions, but neither approves it on the user's behalf nor claims durability tests passed.

## Findings and dispositions

Confidence measures confidence in the planning finding, not likelihood of a runtime defect. These are obligations and unresolved validation risks; there is no source implementation to diagnose.

| ID / classification | Evidence and consequence | Recommendation / acceptance gate |
| --- | --- | --- |
| B01 · resolved for Sessions 1–6 | User explicitly approved T01–T12 and scoped disposable laboratory S3 fixtures. | Windows packaging is authorized; native gates await the VM. Phase 1B, commits, pushes, provisioning and final infrastructure teardown remain unapproved. |
| B02 · blocker to 1A completion | [Roadmap](ROADMAP.md#phase-1-reliable-file-management): Windows VM is planned, not available. Confidence high. | T11/T12 must produce actual Windows runtime, vault, packaging and accessibility evidence. Fedora work can start first. |
| R01 · significant · high confidence | [ADR-002](DECISIONS.md#adr-002-retain-the-preferred-managed-desktop-stack) leaves exact packages proposed. Metadata does not establish combined runtime compatibility. | T01 verifies patched SDK, exact restore, transitive graph, licenses/advisories and builds. Test-framework major versions must agree with Avalonia's headless adapter. |
| R02 · significant · high confidence | [Credential model](SECURITY_MODEL.md#credentials-and-portable-operation) depends on two native services. Portable ZIP does not make remembered secrets portable. | T04/T05 test explicit target binding, unavailable/locked vaults, cancelled unlock, storage failures and no plaintext fallback. Native success requires each OS. |
| R03 · significant · high confidence | [S3 semantics](ARCHITECTURE.md#browsing-and-object-semantics) requires exact keys and pagination. SDK v4 nullable responses, encoding and provider pagination differences can violate that boundary. | T06/T07 validate required fields, retain unknown optional values, preserve opaque tokens and exact keys, bound pages and reject malformed/repeated continuation states. |
| R04 · significant · high confidence | [ADR-006](DECISIONS.md#adr-006-recovery-before-advanced-resume) forbids silent replay. SDK retry and multipart helpers can conceal attempts. | Before 1B mutations, review per-operation SDK retries and ambiguous outcomes; before 1C, crash-test journal/reconciliation boundaries. Do not derive write policy from the read-only retry default. |
| R05 · significant · high confidence | [ADR-007](DECISIONS.md#adr-007-json-settings-plus-sqlite-journal) has not fixed durability/migration semantics. Microsoft.Data.Sqlite async calls execute synchronously. | Keep SQLite off the UI thread; use the durability design below. Validate native packaging, disk-full, crash, migration and corruption behavior before journal-backed writes/recovery ship. |
| R06 · significant · high confidence | [Live-test contract](TEST_STRATEGY.md#live-integration-access-and-safety) requires explicit opt-in and protected shared data. A convenient fixture can accidentally contact or mutate a real provider. | T09 gates before credential resolution/client construction, allowlists read operations, has no setup/cleanup writes and fails an opted-in run with invalid configuration. |
| R07 · significant · medium confidence | [PRD N01–N06](PRD.md#non-functional-targets) are unmeasured proposals. Per-row HEAD calls, stale results or retained pages can hide unbounded work. | T07/T08 measure synthetic pagination and cancellation; T10/T12 record actual package/startup/memory results. No HEAD per listing row; discard results from superseded sessions. |
| O01 · optional improvement | A second independent engineering opinion would improve review coverage. | Obtain when available; not a reason to repeat discovery or block read-only foundations. |
| O02 · optional improvement | Generic Host, ORM, retry library, AOT and a plugin system add work without a demonstrated 1A need. | Defer. Constructor injection, the SDK and a small explicit composition root suffice initially. |

## Project and dependency plan

Target layout; Session 1 created the production projects plus Core.Tests and Desktop.Tests. Remaining test projects belong to later sessions:

```text
BucketBus.slnx
global.json                       patched .NET 10 SDK pin
Directory.Build.props             net10.0, nullable, consistent build settings
Directory.Packages.props           exact central package versions
src/BucketBus.Desktop/             Avalonia, view models, composition root
src/BucketBus.Core/                provider-neutral models and workflows
src/BucketBus.S3/                  SDK translation and S3 sessions
src/BucketBus.Platform/            profiles, OS secrets; journal added in 1C
tests/BucketBus.Core.Tests/
tests/BucketBus.S3.Tests/           offline SDK/wire fixtures
tests/BucketBus.Platform.Tests/
tests/BucketBus.Desktop.Tests/     headless UI/view-model behavior
tests/BucketBus.S3.IntegrationTests/ explicit opt-in, real provider
```

Desktop references Core, S3 and Platform only to compose them; view models consume Core contracts. S3 and Platform reference Core, never each other or Desktop. Core exposes no SDK/Avalonia types. Create only contracts exercised by 1A: profile/session identity, credential acquisition, paged listing and on-demand metadata. Add writes, journals and future authorities when their milestones use them.

Candidates below were verified against official release/NuGet metadata on the review date. **Session 1 subsequently verified the selected T01–T03 dependency graph through restore, build and tests**; see [actual versions](SESSION1_IMPLEMENTATION.md#verified-versions). Future dependencies remain proposed. Recheck availability, dependency graph, licensing and advisories in T01, then commit exact versions and lockfiles only when authorized. Do not float to a different major version automatically.

| Dependency | Recommended candidate | Placement / qualification |
| --- | --- | --- |
| .NET SDK | **10.0.113**, `latestPatch` roll-forward within that feature band | Patch the observed older 10.0.1xx SDK during authorized implementation. The 10.0.4xx band is also available but need not be adopted for this milestone. Runtime servicing candidate: 10.0.13. [Release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json) |
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter | **12.1.4**, same version throughout | Desktop; matching Avalonia.Templates 12.1.4 for bootstrap. Inter font is optional if unused. [Desktop package](https://www.nuget.org/packages/Avalonia.Desktop/12.1.4) |
| CommunityToolkit.Mvvm | **8.4.2** | Desktop only; observable state/commands without a second reactive framework. [Package](https://www.nuget.org/packages/CommunityToolkit.Mvvm/8.4.2) |
| AWSSDK.S3 | **4.0.104.5** | S3 and explicitly scoped test tooling. Requires AWSSDK.Core >=4.0.102.12 and <5.0.0; lock candidate **4.0.102.12**. [Package](https://www.nuget.org/packages/AWSSDK.S3/4.0.104.5) |
| Microsoft.Extensions.DependencyInjection, Logging, Logging.Debug | **10.0.13** | Desktop composition/log setup; Logging.Abstractions 10.0.13 only where actually consumed. [DI package](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection/10.0.13) |
| Tmds.DBus | **0.95.1** | Proposed Linux Secret Service binding in Platform; review runtime code generation, maintained API and actual desktop behavior. No AOT commitment. [Upstream](https://github.com/tmds/Tmds.DBus), [package](https://www.nuget.org/packages/Tmds.DBus/0.95.1) |
| xunit.v3 / xunit.runner.visualstudio / Microsoft.NET.Test.Sdk | **3.2.2 / 3.1.5 / 18.11.0** | Use the .NET 10 VSTest path consistently. The deliberate xUnit v3 baseline aligns with Avalonia's adapter; do not infer compatibility with the newer xUnit major without restoring/testing it. [xUnit](https://www.nuget.org/packages/xunit.v3/3.2.2), [runner](https://www.nuget.org/packages/xunit.runner.visualstudio/3.1.5), [test SDK](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/18.11.0) |
| Avalonia.Headless.XUnit | **12.1.4** | Desktop.Tests; metadata depends on xunit.v3.extensibility.core >=3.2.2. Run actual headless tests to validate the combination. [Package](https://www.nuget.org/packages/Avalonia.Headless.XUnit/12.1.4) |
| Microsoft.Data.Sqlite | **10.0.13**, reserved for 1C | Not needed for read-only 1A. Metadata brings SQLitePCLRaw 2.1.13 native dependencies; qualify both OS publish outputs when adopted. [Package](https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.13) |

Use BCL System.Text.Json for versioned settings; no extra configuration or JSON framework. Windows credential access uses narrow native interop with OS guards and safe resource release. Prefer existing SDK/OS implementations over handwritten signing, crypto or credential-file parsing.

## Implementation contracts

### Bootstrap, configuration and credentials

Use Avalonia's desktop lifetime, platform detection, compiled bindings and MVVM. Construct one validated DI container at startup; inject workflow dependencies into view models, without service-location or SDK calls in views. Design-time data must never resolve credentials or connect. Cancel and dispose profile sessions on replacement and shutdown. [Avalonia DI guidance](https://docs.avaloniaui.net/docs/app-development/dependency-injection)

Store non-secret versioned profiles atomically under per-user application data, not beside the portable executable. A profile has an immutable ID/revision, explicit endpoint, region, addressing style, optional initial bucket/prefix and a local credential reference. Reject malformed URLs and secret-bearing URL components. Endpoint edits disconnect the profile and require explicit credential rebinding; a matching display name is not authority. Profile import/export UI can wait, but the storage model must not embed secrets or trust imported vault references.

Credential contracts distinguish found, missing, locked, unavailable, denied and cancelled outcomes. Remember only static credentials when the user selects it; manual session-token credentials remain memory-only under the proposed MVP model. Windows uses current-user Credential Manager, including native blob-length checks and proper CredFree handling. Linux uses Secret Service and its unlock/prompt protocol; do not shell out with secrets in arguments. Session-only fallback requires explicit choice. Test store/write failures as well as reads. Native integration tests may use only disposable application-owned vault entries after the relevant test scope is authorized. [Windows API](https://learn.microsoft.com/en-us/windows/win32/api/wincred/ns-wincred-credentialw), [Secret Service](https://specifications.freedesktop.org/secret-service/latest/)

Logging starts with allowlisted categories, error codes and timing/count fields. Do not pass raw SDK exceptions, headers, bodies, endpoints, keys or credentials to sinks. Debug output is still a disclosure surface. Persistent rotating diagnostics, if added, follow the security model's retention limits. No application telemetry is introduced.

### Read-only S3 and browsing

Create explicitly credentialed clients per profile revision with `ServiceURL`, `AuthenticationRegion` and `ForcePathStyle`; verify the selected package's endpoint/redirect behavior in wire fixtures. Production accepts HTTPS only with normal trust validation. Keep the private laboratory HTTP exception in development/test tooling, excluded from the shipped Desktop composition. No ambient credential chain or authenticated redirect to a new authority. [S3 configuration API](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/S3/TS3Config.html)

Use SDK `ListBucketsAsync`, `ListObjectsV2Async` and `GetObjectMetadataAsync` (the HEAD operation). Explicit bucket connections skip ListBuckets and do not require a preliminary metadata/HEAD permission. Account bucket discovery and object listing each retain their own paging semantics. Test modern AWS bucket pagination separately from the chosen RGW behavior; any legacy read fallback must be evidence-based, bounded and never triggered merely by access denial.

SDK v4 response values and collections can be null. Preserve unknown size/time separately from zero; treat optional absent collections as empty only where the protocol permits. Missing required identity or a truncated page without a valid next token is a protocol error. Reject repeated continuation tokens without an infinite loop. Retain keys exactly, including NFC/NFD Unicode distinctions, percent/plus characters, slashes and control characters; escape display only. Verify one decoding boundary using actual SDK serialization/parsing fixtures. Prefix rows and real object rows must remain distinct. [SDK v4 changes](https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/net-dg-v4.html), [ListObjectsV2 API](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/S3/MIS3ListObjectsV2AsyncListObjectsV2RequestCancellationToken.html), [metadata API](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/S3/MIS3GetObjectMetadataAsyncGetObjectMetadataRequestCancellationToken.html)

Initially propose SDK standard retries with at most **two retries / three total attempts**, and a linked **30-second total deadline per read-only listing/metadata operation**, adjustable after measurement. These are testable starting settings, not workload-derived limits or streaming-transfer policy. Pass cancellation to every request, inspect the SDK's actual attempt count and prevent outer retry multiplication. Do not assume synchronous HTTP timeout properties govern every async/body path. [SDK retry and timeout guidance](https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/retries-timeouts.html)

Use the existing proposed 1,000-row page and five-page pane cache limits. Keep token history/cache bounded and label partial sorting/filtering. On navigation, refresh, profile change or cancellation, discard late results from the previous session/generation. Metadata is on demand, never HEAD-per-row. The shell offers keyboard navigation and clear disconnected/loading/empty/denied/error states; transfer actions remain unavailable until implemented. The full local file-transfer pane and drag/drop remain later work.

### Transfer and SQLite review: requirements for 1B/1C

No transfer code or SQLite journal is needed to prove 1A. Retain the approved ADR-006 safety contract. Use explicit low-level multipart operations when implementing transfers so upload IDs, part identity, attempts, cancellation and completion are visible to the journal; do not adopt an opaque transfer helper without proving those hooks. Establish per-operation retry policy before enabling writes, including potentially non-idempotent versioned PUTs and ambiguous multipart initiation/completion. A transport error or successful HEAD with similar size/ETag is not, by itself, proof of the intended write's outcome. Conditional-write, checksum/trailer and abort behavior require identified Ceph and AWS evidence. Preserve checksum/length verification, stable source bytes and safe local commit semantics.

SQLite remains the recommended mechanism, without an ORM. Proposed design: per-user local disk, single writer/instance lock, short explicit transactions, WAL with `synchronous=FULL`, bounded worker queue and awaited durable intent **before** remote side effects. Microsoft.Data.Sqlite executes async methods synchronously; isolate this work from the UI. Do not hold a database transaction across network operations. FULL is a requested durability mode, not proof against faulty storage or all power failures. [Async limitation](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async), [SQLite durability modes](https://sqlite.org/pragma.html#pragma_synchronous)

Schema versioning must reject newer unknown schemas, migrate transactionally with a consistent backup, preserve pending/uncertain records and never recreate a failed database silently. Back up with a SQLite-consistent method, not just copying the main file while WAL is active. Disk-full or commit failure prevents new remote mutations. Corruption enters a recovery-required state that preserves the original files and blocks automatic job execution. Recovery must preserve known cleanup obligations; a missing/corrupt journal cannot establish that remote work is safe to replay. Inspect crash windows before send, after remote acceptance, before local result commit, during migration and around upload-ID persistence. Whole-object restart still requires reconciliation, cleanup/conflict checks and explicit user confirmation. Cross-restart partial-byte resume stays deferred.

## Independently testable tasks

Each task includes its tests in the same change. Paths below are proposed areas, not existing files. Avoid empty future interfaces or tests that merely reproduce implementation. Task completion requires recorded outcomes, not just scaffolding.

| ID | Work / proposed area | Depends on | Acceptance criteria |
| --- | --- | --- | --- |
| T01 | Toolchain and dependency gate; root SDK/package configuration | Coding approval | Install/update only within authorized scope; exact restore succeeds; inspect licenses/advisories and transitive graph; validate Avalonia/xUnit combination. Record version changes with reasons. No runtime compatibility claim from metadata alone. |
| T02 | Four modules and offline test infrastructure; `src/`, `tests/` | T01 | Solution builds with nullable enabled; dependency directions are enforced in review/build checks; meaningful initial model/validation tests run. Live-test discovery without opt-in causes no credential/network access. |
| T03 | Avalonia shell, DI and diagnostics; Desktop | T02 | Linux headless shell boots; constructor injection works; design mode stays offline; keyboard focus/loading/error states exercised; synthetic secrets and raw server errors cannot escape diagnostic boundaries. |
| T04 | Profiles and explicit credential binding; Core/Platform | T02 | Round-trip and atomic-save tests; unsupported schema and interrupted save handled without silent data loss; unsafe endpoints rejected; target edits detach credentials; secret-free serialization; independent profile sessions. |
| T05 | OS credential adapters and session-only mode; Platform/Desktop | T03, T04 | Fake-backed tests cover missing/locked/denied/cancelled/write-failed stores and explicit fallback. Actual Linux tests use an interactive user session; actual Windows tests wait for T12. No plaintext fallback or saved temporary credentials. |
| T06 | Read-only S3 session and normalization; S3/Core | T04 | Wire fixtures prove path-style/signing target, exact keys, optional/null fields, direct-bucket flow, pagination, 403 classification, redirect rejection, cancellation and finite attempts. No mutation methods introduced. |
| T07 | Connection and object browser UI; Desktop | T03, T05, T06 | Explicit connect/disconnect; page/prefix navigation and on-demand metadata; bounded cache; late responses cannot overwrite current navigation; keyboard equivalents; no write controls presented as functional. |
| T08 | Fault, security and scale regression suite; offline test projects | T06, T07 | Test matrix below passes; million-key synthetic stream remains bounded without materializing it; malformed/slow responses fail clearly; no secret logging; headless results explicitly exclude OS accessibility proof. |
| T09 | Opted-in live RGW read tests; S3.IntegrationTests | T06, T08 | Guards evaluated before credentials/client/network. Opt-in off skips with zero I/O; opt-in on with missing config/access fails or is explicitly blocked. Only approved reads, bounded pages and optional metadata of an existing object; no writes/resource setup/cleanup. Record SDK-specific results privately. |
| T10 | Fedora desktop and Linux publish evaluation | T05, T07, T08 | Record exact OS/session/native dependencies; run real shell and Secret Service flows; publish/run self-contained linux-x64; inspect dependency/native assets; record startup/memory/package and keyboard/accessibility observations. Missing display is a blocked runtime check, not a pass. |
| T11 | Windows build and ZIP preparation | T03; final artifact also T05–T08 | Session 6 Fedora cross-publish produces the self-contained win-x64 ZIP, notices and locked workflow. Actual Windows restore/build/offline tests remain BLOCKED alongside T12. Construction is not native compatibility evidence. Infrastructure needs separate authority. |
| T12 | Windows 11 VM exit evaluation — BLOCKED, VM unavailable | T11, available authorized VM | Standard-user clean launch without installed .NET/elevation; read-only/non-ASCII application path; Credential Manager lifecycle and persistence outside ZIP; mixed DPI, keyboard and Narrator/NVDA core browsing; PRD startup/memory/size samples with results and failed-budget disposition. No VM provisioned by this plan. |

## Test design and failure paths

This matrix describes the full Phase 1A target. Session 1 supplies shell initialization/state, project-boundary, diagnostics and focus tests; Session 2 adds profile/credential offline coverage. Session 3 implements the read-only provider paths and exercises actual SDK serialization/parsing through offline fixtures; [executed evidence](SESSION3_IMPLEMENTATION.md) does not establish live AWS/Ceph compatibility. Session 4 adds the graphical browser, generation/cancellation protection, bounded cache and offline SDK/headless regressions. Session 5 identified Ceph read/pagination/metadata checks pass; AWS, restricted identities and live fault tests remain **GAP**; native credential/desktop qualification remains **BLOCKED**. See its implementation evidence for exact limits. No LLM features or eval suite are in scope. There are no tests to retire.

```text
Profile input -> validate -> explicit credential lease -> bound S3 session
     | invalid        | locked/denied/cancelled               |
     +-> explain      +-> explicit session-only or stop       v
                                                   bounded page request
                                                     |       |       |
                                                 success   403/TLS  timeout/cancel
                                                     |       |       |
                                              validate/map  explain bounded stop
                                                     |
                                             current generation? --no-> discard
                                                     | yes
                                             virtualized bounded pane

Live-test entry -> opt-in? --no-> skip before all external I/O
                     | yes
               validate approved target/source -> allowlisted GET/HEAD only
```

| Proposed test file / layer | Inputs and expected behavior | Protects / evidence limit |
| --- | --- | --- |
| `Core.Tests/ConnectionProfileTests.cs` · unit | Unsafe URL forms and target edits reject/detach binding; two profile IDs cannot share authority accidentally. | Credential target isolation; no OS vault claim. |
| `Platform.Tests/ProfileStoreTests.cs`, `SecretStoreTests.cs` · unit + OS integration | Interrupted save preserves valid prior state; storage failure is visible; locked/cancelled/unavailable vault cannot trigger plaintext save; owned test entries only. | Secret/config durability; fake stores do not prove native protection. |
| `S3.Tests/ListingWireTests.cs` · SDK wire fixture | Empty/null/partial metadata, malformed XML, repeated/missing tokens, short nonterminal pages, object/prefix collisions, NFC/NFD and `%`/`+` keys. | Exact identity and termination; no Ceph/AWS compatibility claim. |
| `S3.Tests/RequestPolicyTests.cs` · transport fixture · CRITICAL | Explicit endpoint/credentials, denied ListBuckets with direct-bucket success, cross-host redirect, invalid certificate, enterprise test-CA trust, delayed/stalled response and cancellation during backoff. | No credential forwarding/TLS bypass; total attempt/deadline bounds. Fixture trust is isolated, never global bypass. |
| `Desktop.Tests/BrowserTests.cs` · headless | Older request finishes after navigation; cancel/refresh while page arrives; huge synthetic listing; denial and empty state differ. | Correct visible session, responsiveness and bounded retention; no real screen-reader/desktop proof. |
| `S3.IntegrationTests/ReadOnlyQualificationTests.cs` · opt-in live · CRITICAL | Opt-in absent => no credential or network access; invalid opted-in config => failed/blocked, never pass. Approved target => bounded read calls; empty bucket => metadata case explicitly skipped. | Live boundary and observed SDK reads only; no inferred write, pagination, denied-permission or TLS evidence. |
| Desktop/vault manual matrix · both OSs | Portable launch, vault lifecycle, locked session, keyboard, DPI and assistive technology against published artifact. | Runtime/platform evidence for the tested configuration only. |

For every new test: protect an externally observable outcome or safety invariant; inject failure at the closest reliable seam; state what the fixture cannot prove. Combine related cases in parameterized tests instead of creating redundant suites. Keep unit tests independent of environment profiles and live connectivity.

Implemented read-only harness contract (see [current instructions](LIVE_QUALIFICATION.md)): require `BUCKETBUS_RUN_LIVE_TESTS=1` plus an explicit path to an ignored local configuration containing the approved endpoint, region, addressing style, bucket and selected AWS credential-profile name. It contains no credential values. Resolve only that named profile after the opt-in/target guards; missing or unsupported profile sources stop the run without ambient fallback. Session 5 uses HTTPS only with normal verification; no HTTP exception was implemented. Account discovery is separately selectable when authorized; direct-bucket tests remain independent. Keep the live project out of ordinary unit-test commands and retain its internal guard even when selected directly. Record skipped/blocked cases and SDK version without raw keys, metadata or credential-source names in public output.

Initial synthetic network cases, **proposed rather than measured network characteristics**: immediate response; 250 ms and 1 s header delay; a stall beyond the proposed read deadline; disconnect before headers and mid-response; restoration before/after retry exhaustion. Injected transport errors are not actual packet loss. At 1B/1C, use separately authorized isolated impairment to add 1%/5% packet loss, 1/10 Mbit/s bandwidth, long progressing transfers, VPN reachability changes, multipart cancel/failed abort and ambiguous completion. The existing broader [network matrix](TEST_STRATEGY.md#lan-and-wanvpn-qualification) remains mandatory. Enterprise proxies/interception are unresolved, not assumed present.

## Implementation session sequence

1. **Foundation:** T01–T03. Deliver a buildable shell and executable offline tests. Resolve package/API mismatches before adding workflows.
2. **Profiles and credentials:** T04–T05. Deliver explicit target binding and secure/session-only credential paths. Mark unavailable native checks blocked; do not claim completion from fakes.
3. **S3 reader:** T06. Prove protocol mapping and bounded request behavior with offline fixtures before live use.
4. **Browser:** T07–T08. Deliver usable read-only navigation and adversarial/headless coverage.
5. **Live reads and Fedora:** T09–T10. The read-only harness never mutates. Updated Session 5 authority permits separate opt-in fixture writes in a dedicated laboratory bucket with an ownership manifest and confirmed cleanup; preserve shared manual data and laboratory infrastructure. Record SDK, write-tooling and desktop evidence separately.
6. **Windows exit:** T11–T12. T11 can start after the shell; run final artifact checks after browser completion. T12 waits for the Windows VM. Record a conditional development alpha if that gate remains blocked.

These are coding-session boundaries, not mandatory separate PRs or time estimates. No commit/push is authorized here. Each session should read this plan and relevant safety contracts, implement only its tasks with tests, report results/limitations, and leave the repository reviewable. Avoid another full product review unless a concrete high-impact failure invalidates the selected approach.

## Remaining questions and deadlines

| Register | Remaining question / validation | Deadline and effect |
| --- | --- | --- |
| Q01 | Exact build/package provenance, configuration and restricted identities; isolated write scope approval | Before 1B writes and any provider support claim; Session 5 additionally authorizes isolated laboratory S3 fixture writes; application write/recovery implementation and final infrastructure teardown remain outside that approval. |
| Q02 | Maximum object size/count, interruption frequency and retransmission burden | Fixture plan before 1C; alpha feedback and before 1E. ADR-006 scope remains approved. |
| Q03 | Is SSO/role acquisition mandatory for launch? Manual temporary credential proposal remains pending | Before committing launch authentication scope; not a blocker to static-key read-only foundations. |
| Q04 | Exact supported Fedora desktop/Windows build; Linux binding and package/runtime compatibility | T01/T05/T10/T12 during 1A. Package metadata checked; native execution remains unverified. |
| Q05 | Accept/revise PRD numeric budgets after initial measurements | Use proposed budgets as T10/T12 measurement targets; explicit disposition before closing 1A, transfer budgets at 1C. |
| Q06 | Windows VM and AWS account/access/budget | VM before T12/1A exit; AWS before real 1B runs. Neither is assumed available. |
| Q07 | Signing identity/funding/protected release route | Before 1E/public artifacts. Unsigned internal evaluation is not public release qualification. |
| Q08 | Approve SQLite mechanism and verify durability/migrations/native packaging | Engineering review completed conditionally; selection approval and crash/corruption proof before 1C persistence acceptance. |
| Q09 | Proxy/authentication/interception and explicit encryption-header requirements | Before 1B network/auth qualification plan; supported configurations need actual tests. |
| Q10 | Invalid local-name prompt/skip policy | Before 1B downloads; not part of read-only 1A. |
| Q11 | Maintenance capacity and release-response commitments | Before dates or enterprise support promises. |

## First internal alpha and exit evidence

The first useful alpha is a **read-only development build**: secure explicit profiles, bucket/prefix navigation and on-demand metadata. It does not expose uploads, deletes or restart recovery. Existing CLI success reduces access uncertainty but does not count as SDK evidence. An authorized private HTTP test harness can qualify SDK reads; it cannot supply production TLS evidence or imply the release UI accepts plaintext endpoints.

Fedora can immediately support implementation, offline tests, SDK fixtures, read-only live tests when opted in, and Linux publishing after coding approval. GUI/vault runtime validation requires an actual user desktop; Windows native behavior requires the planned Windows VM. A Linux-only or cross-published alpha must be labeled accordingly. The first alpha exposing writes/recovery later must satisfy integrity, overwrite, ambiguity and durable-state gates; no unsafe interim replay behavior is acceptable.

Close 1A only with: exact dependency/build records; offline suite results; guarded SDK read results with limitations; actual Fedora and Windows artifact/runtime/vault checks; keyboard/accessibility assessment; measured PRD platform budgets and disposition; and no known credential leakage, TLS bypass or cross-profile authority defect. Package changes or provider-specific workarounds require a scoped re-review, not a new discovery cycle.
