# Phase 1C.2b — controlled download retries

Implemented for review on 2026-10-26. No live Ceph access, deliberate network/VM
changes, commits or pushes. Four modules and the existing sequential queue remain.
No packages, settings framework or transfer features outside this increment added.

## Behavior and exact eligibility

A download gets **three total attempts**, including its first attempt. Backoff after
attempts 1 and 2 is respectively 1s and 2s, plus independent 0–250ms jitter. Waits
are asynchronous and cancellation interrupts them. One queue item occupies the
worker through its attempts and delays; Pause after current lets that item finish.
Exhaustion stops the queue and retains later Queued jobs for explicit continuation.

| Retry candidate | Classification |
| --- | --- |
| Typed HTTP connection/DNS failure | ConnectionInterrupted (`HttpRequestError.ConnectionError` / `NameResolutionError`) |
| Known socket connection aborted/reset/refused, network down/unreachable, host unreachable/not found, temporary DNS or timed-out socket | ConnectionInterrupted |
| Premature HTTP response ending, explicit short body against declared Content-Length | TruncatedResponse |
| IOException originating in remote response-stream reading, absent stronger TLS/checksum/protocol evidence | ConnectionInterrupted |
| Non-user cancellation from stalled-read or per-attempt deadline | Timeout |
| HTTP 429 | RateLimited |
| HTTP 500, 502, 503, 504 | TransientService |

Other cases are **not** retry candidates: user cancellation; HTTP 401/authentication
codes; 403; 404 (ObjectMissing); 400/408/409/501/505 and all other non-allowlisted
statuses; TLS/secure-handshake errors; generic/unknown transport, malformed-response
or protocol errors; checksum mismatch/SDK invalid-checksum evidence; extra bytes or
missing/invalid length; local conflict, disk-full, permission, unsafe filesystem,
source/profile/destination changes; unknown receipt or failed/uncertain cleanup;
commit/publication uncertainty; journal failures. HTTP status and exception types
are used, never human-readable exception messages. Typed .NET error categories are
[documented here](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httprequesterror?view=net-10.0).
Unknown nested exceptions remain conservative. Retry-After headers are not interpreted.

S3 SDK retries remain zero for GET and PUT; each GET attempt creates a fresh client/
transport, preserving an exact physical-request budget at the SDK transport boundary.
Uploads never enter this loop. If-None-Match, signatures, explicit credentials, source
validation/read-back and Outcome Unknown handling remain; PUT is never replayed after
lost response, timeout, cancellation or HTTP 5xx. Explicit upload reconciliation remains
read-only and separate from download retries.

## Whole-file integrity, ownership and durability

Every retry creates new empty output, requests the complete object with no Range,
and verifies that response's length/available checksum evidence before native
no-replace publication. A mutable key may return newer bytes on a later attempt:
no version pinning, cross-attempt identity or partial resume is claimed. Existing
30-second read-idle and two-hour per-attempt total deadlines remain. Three attempts
can therefore last up to roughly six hours plus bounded delays; no shorter overall
job deadline is introduced.

Before backoff, the previous lease must finish cleanup with no uncertainty and a
recorded receipt. Read-only native checks verify the old temporary entry is absent,
final name absent and parent identity unchanged. No scan or recovery deletion runs.
Before the next output mutation, creation repeats those checks on the pinned parent.
A new path cannot redirect retries between preflight and native creation. Cleanup,
ownership, destination or publication uncertainty stops with preserved evidence.
Native no-replace commit remains the final collision guard if another process
creates the final name during GET.

**Schema remains 2.** Optional typed JSON fields add Attempt, MaximumAttempts,
RetryError, RetryAt and RetryLimitReached. Old schema-2 payloads default these fields
and remain readable; no DDL or migration is required. Target/profile/operation ID
and queued position stay unchanged. RetryDelay is an appended phase. Retry intent
is committed after cleanup/checks and before waiting; the next attempt is committed
before output creation. The old receipt remains until the new receipt is durably
captured, since the prior file is confirmed cleaned. Journal failures block effects.

Startup maps an active attempt or RetryDelay to Interrupted and performs no network,
cleanup or delay scheduling. A crash after new output creation but before its receipt
is saved retains the older evidence but disables restart: that receipt cannot prove
ownership of the new file. Known creation/publication gaps require manual inspection;
unknown files are never automatically removed. Normal stopped/cancelled attempts
retain counts, cause and any cleanup obligation. Existing hardware/power-loss and
Windows native qualification limits still apply.

## Executed validation

Fedora development environment, SDK 10.0.113, isolated local files/SQLite and fake
credentials. All S3 requests use synthetic HTTP handlers, never a live endpoint.

| Check | Result |
| --- | --- |
| Main locked restore / Release | Passed; zero warnings/errors |
| Offline suite | 463 passed: Core 70, Platform 120, S3 109, Desktop 164; one opt-in native vault test skipped |
| New cases | 69 policy, status/type, native fault, retry/crash, journal and UI cases |
| Integration locked restore / Release / guards | Passed; 30 guards, no live tests |
| Both NuGet audits including transitives | No known vulnerabilities reported by the feed at this check |
| Actual Fedora GUI | Not executed; no graphical session in this environment |
| Windows native behavior | BLOCKED; Windows 11 VM unavailable |

Tests cover successful second whole-file GET after connection/truncation/closure/
timeout, precise HTTP status/type allowlists, exhausted budgets, cancellation during
GET/delay, pause, FIFO, local disk/permission/conflict, cleanup failures, absent/unknown
receipts, substituted parent/final/temporary names, ambiguous commit, profile changes
and durable retry-barrier failures. Subprocess termination during backoff, a second
GET and the new-temp receipt gap preserves interrupted identity and waiting jobs with
zero automatic I/O or cleanup. Legacy schema-2 payloads load unchanged structurally.
PUT fault tests require one PUT even for timeout, cancellation, lost response or 500.
Headless UI tests show attempt/backoff/exhaustion text and cancellation on shutdown.

## GUI smoke test and limits

On Fedora, and later the authorized Windows VM, use disposable files and an
independently authorized test target or synthetic fault fixture:

1. Queue downloads and Start. Check attempt 1/3, progress/size preferences and normal
   verified completion, with only one active item.
2. With a controlled transient GET failure, check backoff text and attempt 2/3;
   confirm a complete final file and no exposed partial final name.
3. Pause during backoff: current item may finish its attempts, subsequent jobs stay
   queued. Cancel during backoff or close: no later GET should start.
4. Exhaust three attempts; verify the reason/limit in history and untouched waiting
   jobs. Existing destination, checksum, auth and cleanup failures must not retry.
5. Terminate a disposable process during delay/attempt, reopen offline and inspect
   Interrupted plus retained queue. Nothing should start automatically. Confirm
   uncertain uploads still require reconciliation and never gain a retry action.

Retry eligibility is deliberately narrow. An unknown/protocol or strong checksum
failure can stop even when an operator suspects a network interruption. Filesystems
without suitable ownership receipts cannot automatically retry. No live WAN/VPN,
provider-specific retry compatibility, native Windows, GUI accessibility or hardware
power-loss claims are made by these offline results. Stop after Phase 1C.2b for review.
