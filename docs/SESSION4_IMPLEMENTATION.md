# Session 4 implementation evidence — T07–T08

Executed 2026-10-22. Sessions 1–3 were accepted for continued development and the user explicitly approved T07–T08. The read-only browser and regression suite are implemented for review. Architecture B, ADR-006, HTTPS validation, explicit credential binding and existing module boundaries remain unchanged. Session 5 and later work require separate approval.

## Delivered browser

The remote pane now consumes the real Core session controller and S3 adapter through constructor injection. Choose a saved profile and explicitly **Connect**. Credential acquisition uses the existing profile ID/revision binding; **Allow vault unlock prompt** is an explicit choice. Missing, locked, unavailable, denied, cancelled and failed credential acquisition have fixed, actionable messages. Session-only or replacement credentials are configured through the existing Profiles editor. Reloading profile metadata never resolves credentials or connects.

A configured bucket is listed directly without ListBuckets or preliminary HEAD. Without one, Connect requests bucket discovery. Discovery denial leaves the session available for an explicit bucket entered through **Go**. Bucket/prefix inputs navigate to the exact supplied scope. Enter or double-click a bucket/prefix to open it; **Up** removes one prefix component without normalizing leading/repeated slashes. Objects are selected for properties, never opened or downloaded.

Rows display name/key, byte size, UTC modification time and bucket/prefix/object/marker type. Unknown fields stay unknown. Object and prefix collisions remain separate typed rows. Control/format characters and backslashes are escaped for display; a Unicode-escaped identity field distinguishes visually ambiguous NFC/NFD keys. Raw keys remain separate from presentation and are passed unchanged to Core. Metadata is bound as plain text and retrieved only through **Properties** or Alt+Enter, never per listing row.

The resizable local/remote layout remains. The local pane and transfer area explicitly state that those capabilities are not implemented. No functional upload, download, delete or administration controls were added. Clear disconnected, connecting/loading, empty, ready, denied, cancelled and error states accompany commands.

Keyboard behavior:

| Input | Behavior |
| --- | --- |
| Tab / Shift+Tab | Standard control traversal |
| Ctrl+Tab | Switch local/remote focus; runtime remote focus enters the list |
| Ctrl+L | Focus the active pane's read-only location |
| Arrow keys | Select a remote row |
| Enter / double-click | Open a selected bucket/prefix |
| Backspace in list | Navigate up one prefix component |
| Backspace in text input | Edit text; never navigate |
| Alt+Enter in list | Retrieve selected object's properties |
| Ctrl+R in remote pane | Refresh the current scope |
| Enter in bucket/prefix input | Navigate using those fields |

Navigation handles keys before ListBox consumes Enter, and restores list focus when a replaced page removes the focused row. It does not steal focus from a different control selected while waiting. Refresh restores exact key/type selection if present on the first page and reports when that selection is absent.

## Lifecycle and resource bounds

`RemoteBrowserViewModel` owns navigation, metadata and profile-load generations. Navigation, refresh, selected-profile changes, disconnect and cancellation invalidate superseded work. Metadata also has its own generation, invalidated by selection changes. Late successes and failures cannot overwrite current rows, location or metadata. Persisted changes through ProfileManager invalidate the session and reload profile metadata; reconnect remains explicit. Closing the window cancels pending browser and credential operations before service disposal.

Operations use asynchronous Core contracts off the UI thread. No file persistence, credential-store operations or provider work occurs in views. View code handles only focus, key routing and command invocation. No new Core interface, production project or framework was introduced. The S3 test transport remains internal; friend access was added only for Desktop tests to exercise the actual SDK through the UI workflow. Shipped application configuration has no HTTP exception or TLS bypass.

**Next page** requests one page, at most 1,000 entries. No page is prefetched and buckets are not eagerly enumerated. The remote pane retains at most five pages / 5,000 rows, evicting the oldest when necessary. **Previous cached page** and forward navigation within retained pages require no network. Evicted pages are revisited by refreshing the scope from the beginning; no random-access promise is made. The status explicitly labels page/cache counts and server order, not a whole-bucket total. Sorting and filtering controls are not implemented, so no partial data is presented as a global sort/search.

A virtualizing ListBox renders only visible containers. The SDK continues to own opaque continuation tokens, cycle detection and its finite traversal limit. Pagination failures disable further forward fetching until Refresh; the last valid cached page remains visible with an error. Cancellation requires Refresh before continuing a cursor, avoiding accidental page skips if completion raced cancellation. Session 3's three-send / 30-second read-only policy is unchanged; no UI retry loop was added.

## Executed tests and validation

Fedora 44 x64, .NET SDK **10.0.113**, runtime **10.0.13**. `./eng/validate.sh` completed with the repository build/test telemetry opt-outs:

| Check | Result |
| --- | --- |
| Locked restore | PASS; all eight project lockfiles retained |
| Release build | PASS; zero warnings, zero errors |
| Core tests | 34 passed |
| Platform tests | 38 passed; 1 skipped |
| Desktop tests | 44 passed, including 34 new browser cases |
| S3 tests | 52 passed |
| Total | **168 passed, 0 failed, 1 skipped** |
| NuGet audit, including transitives | No known vulnerabilities reported on 2026-10-22 |

All 134 Session 1–3 tests remain passing. No package versions or package references changed; the resolved union remains 60 package/version pairs. This dated audit result does not guarantee absence of vulnerabilities.

New tests include:

- Actual SDK serialization/parsing through the real ProfileManager, session controller and browser: bucket discovery, direct access after discovery denial, prefix navigation, exact leading-slash/Unicode/percent/plus keys, object/prefix collisions, unknown fields, explicit metadata and metadata denial.
- Actual SDK malformed XML, missing required fields, repeated opaque continuation tokens, three-attempt retry exhaustion, short injected deadline, slow request cancellation and safe error presentation. Existing Session 3 tests retain detailed signature, TLS and wire-level coverage.
- Controlled late completions beyond the SDK boundary: navigation and profile replacement, changing selection during metadata retrieval, persisted profile revision changes, cancellation during credential acquisition and distinct credential failures. These deliberately cancellation-resistant fixtures verify UI generation checks independently of cooperative transport cancellation.
- One million synthetic objects traversed across 1,000 explicitly requested pages. At most 5,000 rows remain cached, no prefetch occurs, previous/next cached navigation performs no additional reads, and a weak-reference/GC check confirms an evicted row is collectable. This is bounded-retention evidence, not a native memory-budget or network-performance benchmark.
- Actual Avalonia controls: profile selection binding; keyboard Connect, prefix navigation, Properties and Up; editing Backspace; loading/error bindings; pane shortcuts; window-close cancellation; literal untrusted text; and virtualized realization of fewer than 61 containers for a 1,000-row page.

No raw exception or provider-body text is surfaced in error messages. Browser operations add no diagnostic logging. Displayed object names and requested metadata are intentionally treated as user-visible data, not executable markup or diagnostic messages.

## Blocked and unexecuted checks

**Native Fedora desktop validation: BLOCKED.** Neither DISPLAY nor WAYLAND_DISPLAY is available. No real graphical launch or screenshots were produced. Headless Avalonia execution is not a native desktop pass. Native Linux Secret Service qualification remains blocked; the native-vault test remains skipped without explicit opt-in.

**Windows 11 native validation: BLOCKED / pending VM.** No Windows packaging or runtime checks were performed. Screen readers, mixed DPI, themes and real platform focus/input behavior remain qualification work.

No live Ceph/AWS requests, existing AWS CLI credential access, resource provisioning or shared object changes occurred. Offline SDK fixtures make no external S3 calls. No claim of Ceph deployment compatibility follows from this session. Session 2's crash-orphan vault cleanup limitation and Session 3's SDK workaround/addressing restrictions remain unresolved or conditional as documented.

## Session 5 prerequisites

Session 5 requires separate approval for an explicitly opted-in, read-only live harness. Bind an identified provider/build/configuration and approved target to its explicit credential source; fail or skip before credentials/network when guards are not satisfied. Keep environment details and raw evidence under ignored `docs/internal/`. Any temporary private HTTP laboratory exception belongs solely in test tooling outside the published Desktop dependency graph; the browser continues to require HTTPS.

Qualify actual discovery/direct-bucket permissions, encoded listings, pagination, metadata/signatures, cancellation and the retained SDK leading-slash workaround against identified targets. Do not create fixtures, mutate shared objects or expand credentials without separate authority. Resolve appropriate HTTPS/TLS, VPN/network and native desktop/vault test access before claiming production usability. Earlier limits on dotted virtual-hosted buckets, the traversal cap, enterprise trust/proxies and native credentials remain in force.

Local browsing, transfer operations, SQLite recovery, administration, live qualification and Windows packaging remain outside Session 4. No commit or push was performed.
