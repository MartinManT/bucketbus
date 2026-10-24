# BucketBus UX specification

Status: DRAFT screen descriptions and flows · 2026-10-21

This is the broader interaction specification. Session 4 implements its read-only browsing subset; [executed evidence](SESSION4_IMPLEMENTATION.md) records headless coverage and native limitations. Transfer and administration flows below remain future designs. [PRD](PRD.md) controls scope. The initial application presents one local pane and one remote pane; independently configurable remote/remote panes are deferred with S3-to-S3 transfers.

## Main screen

All names, paths, objects and transfer values in this wireframe are fictional examples, not local infrastructure or execution evidence.

```text
BucketBus                         Connection: Lab storage     Connected
Connections   Transfer   View   Help                Theme: System
-----------------------------------------------------------------------
LOCAL                                      REMOTE: Lab storage
C:\Users\Alex\Downloads                    bucket: team-data / incoming/
[Back] [Up] [path field]                    [Back] [Up] [bucket/prefix]
Name          Size        Modified         Name          Size   Modified
[..]                                       [..]
archive.zip   450 MiB      ...              logs/         Prefix
image.iso       8 GiB      ...              report.csv     2 MiB ...
                                           [Load more] 1,000 loaded
-----------------------------------------------------------------------
Copy to remote  Properties  Delete          Destination: incoming/
-----------------------------------------------------------------------
TRANSFERS           Running 2   Waiting 3   Needs attention 1
archive.zip  Upload    280 / 450 MiB    18 MiB/s   [Cancel]
image.iso    Waiting  ...
report.csv   Outcome unknown: review destination             [Review]
-----------------------------------------------------------------------
Selected: 2 files, 8.44 GiB       [Open queue] [Connection details]
```

The active pane has a clear focus border and accessible name. The transfer button names the destination, rather than relying on an arrow. Source and destination remain visible during confirmation. Connection identity includes the endpoint in details and all destructive dialogs; a friendly profile name alone is insufficient.

Use standard desktop controls, platform fonts, compact readable rows, restrained icons, adjustable pane split and a collapsible queue. Persist layout per user. Keep destructive actions out of the primary transfer button. No Ceph controls in the normal toolbar. MVP menus do not advertise nonexistent admin functionality.

Remote rows distinguish an object from a prefix through text/icon/accessibility labels. A prefix has no invented size or timestamp. An object named `a` and a prefix `a/` remain distinguishable. “Loaded items” labels accompany client-side filtering and non-key sorts. Do not imply a whole-bucket search or exact total from one page. Selecting all means loaded visible rows; transferring a complete prefix is a separate explicit action that enumerates it.

## Connection manager

The first screen offers New connection and Import profiles. Initial form: display name, provider preset (AWS S3, Ceph RGW, generic S3), endpoint, signing region, access key, secret key, optional session token, optional bucket/prefix, and Remember credentials. A preset supplies suggestions, not an unsupported compatibility claim. Advanced fields include addressing mode, read-only UI preference, and non-secret transfer defaults.

When a bucket is specified, “Connect to bucket” performs only relevant reads there. Without one, try bucket listing. If denied, show: “Your credentials cannot list buckets. Enter a bucket you are allowed to use.” Keep successful authentication distinct from resource authorization where evidence permits; otherwise say the server denied the request without inventing a diagnosis.

TLS errors name the certificate problem and endpoint, with guidance to verify the endpoint or contact the CA administrator. There is no bypass button. Remember-credentials controls explain OS storage and show session-only mode if the vault is unavailable/locked. Session tokens are memory-only in MVP. Imported profiles stay disconnected and require credential selection.

## Primary flows

### Upload

1. Select files or a local directory and activate Copy to remote, F5, or internal drag/drop.
2. Show source, destination endpoint/bucket/prefix, operation count or “discovering,” and empty-directory marker policy. A simple nonconflicting file transfer can enqueue immediately; replacement always requires a decision.
3. Queue begins bounded enumeration/transfers. Directory copy preserves relative hierarchy; symlinks/special files are skipped with visible reasons. It does not synchronize or delete remote data.
4. Existing keys prompt Skip, Choose another name, Replace, or Cancel. “Apply to remaining conflicts in this batch” is scoped to the batch and choice. It never suppresses a later changed-object/precondition warning.
5. Bytes sent can reach 100% while the row says “Finalizing” or “Verifying.” Only confirmed completion becomes Succeeded. A lost completion response becomes Outcome unknown with Review, not a blind Retry button.

### Download

Select exact objects or a prefix, choose the local directory, and review name conflicts. Invalid local names show the exact remote key and proposed explicit mapping, or Skip. A file/directory collision is a decision, not automatic renaming. Partial content stays under an application-owned temporary name; the old target remains intact until a successful approved commit.

MVP supports internal remote-to-local drag/drop. Dropping local Explorer files into the remote pane is proposed for Windows; external remote drag-out to Explorer is deferred because shell promised-file behavior is a separate integration task. Keyboard operations provide full core functionality on both operating systems.

### Failure and retry

Transient retry shows attempt and next retry delay. Permission/TLS/auth failures explain the intervention needed. “Retry selected failures” excludes uncertain writes and never replays successful items. If credentials expire, prompt for a new lease for the same intended identity/endpoint; do not silently use another saved profile.

Closing during active work offers Keep open or Cancel transfers and close. If remote cleanup cannot finish, closing preserves the obligation. A crash/restart opens a recovery summary: interrupted jobs, known unfinished uploads, local partial files, and uncertain outcomes. Under approved ADR-006, the user explicitly confirms restarting interrupted jobs; uncertainty must first be reconciled rather than cleared by that confirmation. Neither VPN reconnection nor reopening the application silently replays uncertain writes. Explain that MVP recovery restarts the affected object from the beginning and can retransmit the whole file; cross-restart partial-byte resume is deferred to Phase 2.

### Delete

Only object-row deletion is available in MVP. Prefix/bucket rows have no delete action. Confirmation includes exact destination, item count and keys, with a scrollable list. State that deletion may be permanent and that older versions may remain; if versioning state is unknown, say so. Show successes and failures separately. Do not offer a fake Undo.

### Properties

Local properties show relevant filesystem metadata. Remote properties fetch metadata on demand and show exact key, size, time, content type, opaque ETag, available checksums, storage class and version ID when known. Unknown permissions/fields are not displayed as false or zero. Copy exact key is explicit; display escaping does not change copied identity.

## Keyboard contract

| Key | Action |
| --- | --- |
| Tab / Shift+Tab | Standard focus traversal |
| Ctrl+Tab | Switch pane focus |
| Enter / Backspace | Open selected directory/prefix / navigate up, outside text editing |
| Ctrl+L | Focus active pane location field |
| F5 | Copy selected items to the opposite pane |
| Ctrl+R | Refresh active pane |
| Delete | Confirm deletion for supported object rows; never when editing text |
| Alt+Enter | Properties |
| Ctrl+A | Select loaded visible rows; label scope |
| Escape | Close a dialog or cancel drag; does not silently cancel all transfers |
| Shift+F10 | Context menu |

Remote F2/F6 rename/move shortcuts are absent in MVP. Keyboard and context-menu actions use the same application commands and validations as drag/drop.

## Accessibility and visual behavior

Use semantic labels and automation peers for panes, paths, rows, progress, and buttons. Preserve focused row identity during paging/refresh; report disappeared items without moving focus unpredictably. Announce major state changes, not every progress tick. Error status uses text and icon as well as color. Dialog focus returns to the initiating control.

Test light/dark/system theme, increased text size, high contrast, reduced motion, 100/150/200/250% DPI, and moving between monitors. Do not rely on a custom-drawn grid without proving keyboard and screen-reader behavior. Current Avalonia documentation describes UIA and AT-SPI support; application accessibility still needs actual assistive-technology tests. [Avalonia accessibility](https://docs.avaloniaui.net/docs/app-development/accessibility)

## Simple and advanced experiences

Simple view exposes connection, navigation, transfer, queue, properties and deletion. Advanced connection details expose signing region, addressing mode, provider evidence and safe diagnostics. Basic safety does not depend on opening advanced settings.

Phase 3 administration is a separately entered workspace, enabled deliberately and bound to a separate authority. Show the authenticated authority and whether the session is RGW Admin Ops or account IAM. Unsupported/unknown features explain their condition; denial is not hidden as nonexistence. Privileged edits require a diff/target review and explicit Apply. Returning to the file manager restores its ordinary connection; admin credentials are never inherited.

## Initial usability questions

Validate whether users understand prefixes, loaded-only sorting, partial batch failure, “Finalizing,” “Outcome unknown,” and restarted-object recovery. Observe the same tasks in WinSCP and at least one other comparator before claiming better usability. The first practical next step is to walk through these screens with one anonymized real transfer scenario, then use the PRD study criteria during an authorized prototype phase.
