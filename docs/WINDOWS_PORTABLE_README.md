# BucketBus Windows evaluation build

Extract the complete ZIP and launch `BucketBus.Desktop.exe` as a normal user.
Keep all bundled DLLs, including `e_sqlite3.dll`, together. No installed .NET SDK
is required. This unsigned pre-alpha has not yet passed Windows 11 native gates.

The executable folder is portable; user data is not. Profiles and the transfer
journal live in the current user's local application data under `BucketBus`.
Persistent secrets use current-user Credential Manager; temporary credentials
remain in memory. Do not copy the journal to a shared/network directory.

Transfers are single-file and create-only. Remote collision protection requires
the provider to enforce conditional PUT. Interrupted work is never replayed at
startup. Expand **Transfer history and recovery** to inspect records, explicitly
reconcile uncertain uploads, or confirm an eligible whole-file download restart.
Unknown uploads cannot be restarted. Existing local files are never replaced.

Close all instances before preserving a journal for diagnosis. Keep the database,
WAL and SHM together; do not delete or replace a corrupt journal to unblock writes.
The journal contains sensitive paths and object names, but no credential values.

See `WINDOWS_VALIDATION.md` for the outstanding native checks. Nothing in this
package authorizes live provider testing or infrastructure changes.
