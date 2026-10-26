# Windows 11 native validation — BLOCKED

The Windows 11 VM is unavailable. Fedora cross-publishing and x64 PE inspection
establish artifact construction, not Windows runtime compatibility.

When an authorized VM is available, use disposable local data and synthetic/offline
transports first. Record OS build, filesystem, package hash and actual results:

- Extract the ZIP to non-ASCII and read-only application locations; launch as a
  standard user without an installed .NET runtime. Check DPI, keyboard and screen
  reader behavior, startup time, memory and responsive shutdown.
- Verify current-user Credential Manager creation/read/delete with explicitly
  opted-in application-owned entries. Never inspect unrelated credentials.
- Run locked restore, Release build and all offline tests. Native SQLite must
  load from the package, create a per-user journal with private inherited ACLs,
  commit transactions, reopen history offline and reject a second journal owner.
- Run the subprocess crash/restart fixtures, including before/after synthetic PUT,
  interrupted downloads and local-commit ambiguity. Verify no automatic replay.
- Exercise Windows handle-based no-replace commit and recovery cleanup, conflicting
  destinations, changed temporary identities, reparse ancestors and cancelled close.
- Confirm a corrupt/future-schema journal blocks transfers and remains preserved.

Native vault, filesystem, journal, GUI and accessibility checks remain pending;
Linux/headless results do not satisfy them. Live tests require separate explicit
opt-in and target approval; this checklist grants neither.
