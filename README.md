# BucketBus

A cross-platform, Commander-style S3 file manager, with Ceph RGW as the initial validation target.

Development is at **Phase 1C.1: durable single-file transfers and conservative crash recovery**. The desktop provides profile/credential management and a read-only S3 browser: explicit connect/disconnect, bucket and prefix navigation, bounded paging and on-demand metadata. The local pane browses directories, files and links. One selected file can be uploaded with a create-only request; one selected object can be downloaded without replacing local data. See [Session 4 results](docs/SESSION4_IMPLEMENTATION.md) for executed offline/headless evidence and limits. Initial SDK reads against one identified Ceph deployment now pass; native Linux qualification is partial, and Windows/AWS qualification remains pending. See [Session 5 evidence](docs/SESSION5_IMPLEMENTATION.md). Session 6 adds a Windows x64 self-contained evaluation ZIP cross-published on Fedora; [Windows native validation](docs/WINDOWS_VALIDATION.md) is BLOCKED until the VM is available. See [Session 6 evidence](docs/SESSION6_IMPLEMENTATION.md).

## Display settings

Open **Settings** in the main window to choose **Human-readable** IEC sizes
(default) or **Exact bytes**, and **Relative names** (default) or **Full keys** for
remote objects. Changes apply immediately to existing rows, progress, history and
cached metadata. Exact byte counts remain in size/progress tooltips and metadata;
the complete S3 key remains in its tooltip and metadata. Folder sizes show `—`;
unavailable object/file sizes show `Unknown`.

Choose **Save preferences** to retain the choices across launches. Preferences use
a separate versioned `preferences.json` in the per-user BucketBus application-data
directory, independent of profiles, credentials and the transfer journal. A missing
or damaged file uses defaults; a save failure is shown and choices remain active
for that session. Relative names only affect presentation: exact keys, numeric
sorting, paging and transfer/recovery identities stay intact. Theme choices are
planned for a later increment.

## Build and test

Install the .NET SDK selected by [global.json](global.json) (10.0.113, or a servicing patch in the same feature band). Package versions are centralized and the resolved graphs are recorded in project lockfiles.

For a custom SDK installation, set `DOTNET_ROOT` to its installation directory and prepend that directory to `PATH` before using the commands below. The system SDK is not upgraded by this repository.

```bash
./eng/validate.sh
```

The script performs a locked restore, Release build and offline test run. It opts out of .NET, Avalonia build and Microsoft Testing Platform telemetry. For manual commands, set `DOTNET_CLI_TELEMETRY_OPTOUT=1`, `AVALONIA_TELEMETRY_OPTOUT=1` and `TESTINGPLATFORM_TELEMETRY_OPTOUT=1` first. Tests also load the repository's opt-out runsettings by default. Restore and dependency auditing require NuGet access; application tests themselves use no external services.

Default tests run offline after restore. They use temporary files, fake vault boundaries and synthetic SDK transports, never external S3 services or real credential stores, and need no graphical display. A TLS rejection fixture opens an ephemeral loopback listener with a disposable self-signed certificate; it makes no external connections or trust-store changes. The native vault test skips unless explicitly opted in. Avalonia headless tests exercise shell/editor initialization, bindings and focus, and do not establish native desktop or screen-reader compatibility.

To run on a graphical desktop:

```bash
dotnet run --project src/BucketBus.Desktop --configuration Release --no-build
```

The shell supports Tab/Shift+Tab focus traversal, Ctrl+Tab to switch panes and Ctrl+L to focus the active pane's location field. In the remote list, use arrows to select, Enter to open a bucket/prefix, Backspace to go up, Alt+Enter for properties and Ctrl+R to refresh. Bucket/prefix inputs accept Enter to navigate. Backspace in text inputs edits text. Native desktop execution requires an available display and Avalonia's platform prerequisites; see [supported platforms](https://docs.avaloniaui.net/docs/supported-platforms).

On Windows, build from PowerShell with the pinned SDK before launching:

```powershell
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
dotnet restore BucketBus.slnx --locked-mode
dotnet build BucketBus.slnx -c Release --no-restore
dotnet run --project src/BucketBus.Desktop -c Release --no-build --no-restore
```

## Structure

- **Desktop**: Avalonia views/view models and the DI composition root.
- **Core**: framework-independent profiles, credentials, browsing contracts and single-transfer coordination.
- **S3**: explicit AWS SDK v4 clients, bounded browsing requests and streamed, conditional single-object transfers.
- **Platform**: atomic JSON profiles, native safe-file operations, Windows Credential Manager and Linux Secret Service adapters.

S3 and Platform depend only on Core. Desktop composes the modules. The [implementation handoff](docs/PHASE1A_PLAN.md), [architecture](docs/ARCHITECTURE.md) and [security model](docs/SECURITY_MODEL.md) govern further work.

Use **Profiles** to create or edit an HTTPS endpoint, signing region, addressing style and optional bucket/prefix. Choose `SessionOnly` to keep credentials in memory or `SecureStore` to use the OS vault. Temporary session tokens require `SessionOnly`. Changing endpoint, region or addressing style detaches the old binding unless you explicitly supply replacement credentials. Profile files contain only metadata and opaque local references; do not treat them as portable credential exports. Session-only bindings need fresh credentials after restart.

Select a saved profile and choose **Connect**; credentials are acquired explicitly for connection and transfer actions. Allow a vault unlock prompt explicitly when needed. A configured bucket is listed directly without account discovery. If discovery is denied, enter an authorized bucket and choose **Go**. Prefixes are exact S3 prefixes, including leading/repeated slashes. **Next page** reads only one page; **Previous cached page** visits retained pages without network access. At most five pages remain cached; Refresh restarts at the first page. Rows remain in server order, with no whole-bucket sort/filter or total implied. Select an object and choose **Properties** to retrieve metadata.

Environment configuration and raw local evidence belong under ignored `docs/internal/`. Never add credentials to documentation, configuration, logs or source control. A separate, explicitly opted-in GET/HEAD harness is described in [live qualification](docs/LIVE_QUALIFICATION.md). Linux self-contained publishing and manual desktop checks are described in [Fedora validation](docs/FEDORA_VALIDATION.md). Native vault opt-in procedures and limitations are in the Session 2 results.

To construct the unsigned Windows evaluation ZIP on Fedora with the exact pinned SDK and Python 3, run `./eng/publish-windows.sh`. The output, checksum and size manifest are under ignored `artifacts/session6/`. No Windows machine, live S3 access or credential store is used by publishing. Distribute the complete ZIP with its notices and follow the Windows checklist; a cross-build is not proof of native compatibility.

Licensed under [Apache License 2.0](LICENSE). Dependencies retain their own licenses; redistribution notices and dependency review are recorded with the implementation evidence.

## Local filesystem browser

The left pane starts at your home directory on actual application startup. Enter an
absolute path and press Enter, use Home/Up or select a filesystem root (Windows
logical drives or Linux `/`). Double-click or Enter opens a selected directory;
Backspace goes up when the list has focus. Ctrl+R refreshes the active pane,
Ctrl+L edits its path, and Ctrl+Tab switches panes. Ctrl/Shift selection and Ctrl+A
apply to visible loaded entries. Column headings sort by name, size, modified time
or type; repeating the heading reverses the sort. Refresh preserves exact selected
paths when still present.

Browsing reads metadata only. Hidden files are included. Links are labelled and
only directory links opened explicitly are followed; no recursive scan occurs.
Broken links stay visible, and disappearing entries show unavailable metadata.
Names retain their native identities; unsafe display characters are escaped.
Paths must be absolute without `.`/`..` segments; use Up for parent navigation.

A listing retains at most 10,000 entries. Larger directories show an explicit partial
listing notice: sorting/selection cover that subset, not the entire directory.
No automatic continuation or whole-directory total is implied. Cancel responds
without waiting for a stalled OS filesystem call; subsequent reads may wait for
that one worker to return. Design-time construction and ordinary tests never browse
your home directory. Native Windows/Fedora keyboard, drive, DPI and accessibility
qualification remains pending; previous Phase 1A native gates remain open.

Manual smoke test on a graphical desktop:

1. Launch, navigate Home → subdirectory → Up; enter a Unicode path and select a root.
2. Refresh, sort each column and use Ctrl/Shift/Ctrl+A selection. Confirm hidden
   entries, links, empty folders and inaccessible/missing paths have clear states.
3. Use Ctrl+Tab/Ctrl+L/Enter/Backspace/Ctrl+R; Backspace in the path edits text.
4. With an independently authorized read-only S3 connection, navigate each pane and
   refresh the other. Confirm their locations and selections stay independent.
5. Cancel a slow listing and close during loading. Check normal laptop sizing,
   scaling, removable drives and native screen-reader behavior manually.

## Single-file transfers (pre-alpha)

Navigate the local pane to a directory and the remote pane to a bucket/prefix.
Select exactly one regular local file and choose **Upload file** (Ctrl+U), or select
one remote object and choose **Download object** (Ctrl+D). The transfer panel shows
the captured source/destination, bytes, progress, cancellation and terminal outcome.
Both panes remain browsable; successful transfers refresh the affected pane only
if you are still viewing that location. There is one active transfer at a time.

Uploads concatenate the current **exact prefix** and native filename without
Unicode normalization. The adapter streams a single PUT with `If-None-Match: *`,
SHA-256/Content-MD5 and **zero SDK write retries**. Source identity/size/timestamps
and full content are checked. Completion requires operation-identity and full
SHA-256 read-back; matching size or ETag alone is never proof. Read-back needs GET
permission and adds download traffic. A denied/lost verification response can leave
**Outcome Unknown**, even after the provider accepted the PUT. Cancel after sending
may also leave Outcome Unknown. Inspect the object before manually retrying;
BucketBus never automatically retries or deletes an uncertain upload.

Create a profile, connect, select a local file and remote bucket/prefix, then choose
Upload. The application uses the profile's explicit credentials immediately; no
qualification file, external procedure or expiration is involved. The server decides
whether those credentials have permission to write.

**Provider compatibility:** collision protection requires the server to honor
`If-None-Match: *`. Conforming providers return 412 for an existing object; a 409
write conflict is also reported without automatic retry. A rejected conditional
upload or required header is reported separately from permission/authentication
failures and uncertain writes; BucketBus never retries with an unconditional PUT.
Some providers return only a generic rejection, which cannot identify the unsupported
header precisely. A provider that silently ignores the condition can overwrite an
existing object. Neither a successful response nor SHA-256 read-back proves that no
previous object was replaced. BucketBus cannot promise universal no-overwrite safety
across S3-compatible providers. Use a provider with documented and tested conditional
write support when protecting existing remote data. [AWS conditional PUT contract](https://docs.aws.amazon.com/AmazonS3/latest/API/API_PutObject.html)

The independently opted-in [laboratory harness](docs/LIVE_QUALIFICATION.md#single-file-write-qualification)
still requires approved targets/scopes and records private compatibility evidence.
Those developer safeguards do not control normal application uploads; one tested
Squid deployment does not establish compatibility with every S3-compatible service.

Downloads use the selected object's last `/` component as a single local name.
Unsafe/reserved names, traversal components, invalid Unicode and unrepresentable
names are rejected, never silently rewritten. Transfers reject symlink/reparse
ancestors and sources, even though browsing can follow directory links. Linux
creates a private random temporary directory with a named payload, then publishes
the open file using atomic no-replace `linkat`; Windows pins ancestors and renames
the open temporary file with `ReplaceIfExists=false`. Existing destinations,
including race-created files/links, remain untouched. Unsupported safe-commit
operations fail closed. Windows transfers currently require local drive paths; UNC/device paths are refused. Only owned temporary output is cleaned up.

Downloads verify actual byte length and compatible server checksum evidence. The
panel states when only length was verified; a locally computed hash is not proof
of remote equality. ETags are opaque. The buffer is 64 KiB. Transfers have a two-hour
total deadline; download response-body reads have a 30-second idle deadline. Single
PUTs are limited to 5 GiB; there is no multipart support or automatic retry.

Transfers are now recorded in a per-user SQLite journal before destination effects.
Expand **Transfer history and recovery** to inspect previous results and unresolved
operations without connecting to S3. Startup never replays work or cleans temporary
files. Explicit upload reconciliation requires the saved operation ID and full
SHA-256 read-back; absence, denial or a mismatch remains unresolved, and upload
restart is unavailable. Unresolved targets also block a fresh upload attempt.
Eligible interrupted downloads can restart from byte zero after confirmation,
matching temporary ownership and checking that the final name is absent. Restart
downloads the current remote object, which may differ from the earlier attempt.
Resolved history can be dismissed without deleting local or remote data.

A journal error blocks transfers and preserves evidence. Close other instances and
preserve the database plus WAL/SHM together for diagnosis; do not delete the journal
to unblock uncertain writes. Ownership gaps or a possibly published final file
remain unresolved. There is no batch scheduler, automatic retry or partial resume.
See [Phase 1C.1 implementation and manual acceptance](docs/PHASE1C1_IMPLEMENTATION.md)
for durability limits and validation. Windows native checks remain BLOCKED; headless
tests and cross-publishing do not prove Windows compatibility.

Manual check on Fedora or Windows:

1. Build/run using the commands above, connect an explicit profile and navigate to
   an authorized bucket/prefix. Use disposable data on a provider that supports
   conditional creation; no qualification-file installation is needed.
2. Select a small local file, upload with Ctrl+U and inspect the Completed and
   SHA-256 verification text. Upload it again: expect Conflict, with original content intact.
3. Navigate locally to a different empty directory, select the remote object and
   download with Ctrl+D. Compare `sha256sum` (Fedora) or `Get-FileHash -Algorithm SHA256`
   (PowerShell). Download again: expect a local conflict without replacement.
4. Exercise zero-byte/Unicode files, cancel a larger transfer, and browse each pane
   while it runs. Confirm cancellation/uncertainty is explicit and no partial file
   appears under the final filename. Do not blindly repeat an uncertain upload.
