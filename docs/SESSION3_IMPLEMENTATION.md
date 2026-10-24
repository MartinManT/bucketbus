# Session 3 implementation evidence — T06

Executed 2026-10-22. The user explicitly approved the read-only S3 backend and offline SDK fixtures, accepting Session 2 with its existing limitations. T06 is implemented for review. Architecture B, ADR-006 and the four production modules remain unchanged. Session 4 and later work require separate approval.

## Implemented behavior and boundaries

- **Core:** `ReadOnlyStorage.cs` defines provider-neutral session, paged listing, object metadata and fixed error categories. `ReadOnlySessionController` resolves credentials for the exact selected profile ID/revision, replaces/disposes sessions, and checks persisted profile identity before and after operations. Successful profile changes invalidate the selected session. Opening a session performs no S3 requests; direct-bucket listing requires neither account discovery nor a preliminary HEAD.
- **S3:** `S3ReadOnlySessionFactory` constructs a fresh AWS SDK v4 client with explicit resolved static or temporary session credentials, custom HTTPS endpoint, signing region and addressing style. There is no ambient credential fallback or shared client across authorities. `S3ReadOnlySession` implements ListBuckets, ListObjectsV2 and explicitly requested GetObjectMetadata only.
- **Desktop:** the composition root registers the factory/controller. No connect controls or graphical object browser were added. Existing profile UI and offline design-time behavior are preserved.
- **Platform:** existing profile persistence and vault implementations are unchanged. Session 2's possible orphaned vault entries after a crash remain unresolved.

Core contains no AWS or Avalonia types. S3 and Platform depend on Core; only the Desktop composition root constructs application services. No transfer, write, bucket-creation, SQLite or administrative APIs were added.

## Exact keys and protocol findings

Core receives exact logical keys without normalization. The adapter requests URL-encoded listing fields and decodes them exactly once only when the response declares that encoding. It preserves literal plus signs, percent signs, Unicode NFC/NFD distinctions, spaces, embedded/leading slashes, directory-marker objects and object/prefix collisions. Objects and prefixes retain separate types. Optional size and timestamp fields remain nullable; absent HEAD Content-Length is captured before the SDK can turn it into zero. ETags remain opaque. Metadata is fetched only on demand and includes available content type, version ID and user metadata.

**Verified SDK incompatibility:** with AWSSDK.S3 4.0.104.5 / AWSSDK.Core 4.0.102.12, the actual SDK pipeline omitted a path separator for path-style metadata requests whose key starts with `/`. This could target a different key and disagree with signing. `ExactKeyS3Client` moves the bucket path into the SDK resource template before signing for this specific request shape. The original key is unchanged; SDK encoding and signing remain in use. The correction is idempotent across retries. A separate test-side SigV4 calculation verifies the signature against the actual outgoing URI, including leading-slash retries. The final transport also rejects HEAD paths that do not match the requested key before sending. Recheck this narrow workaround on SDK upgrades; actual AWS/Ceph acceptance remains untested. The [SDK source](https://github.com/aws/aws-sdk-net/blob/master/sdk/src/Core/Amazon.Runtime/AmazonServiceClient.cs) explains URI composition; the pinned-package fixtures are the version-specific execution evidence.

Listing validation rejects wrong/malformed XML, missing required fields, invalid encoded keys, duplicate entries of the same type and inconsistent continuation state. Missing optional collections are empty, while required object keys and listing identity/truncation fields must be present. Invalid pages yield no partial successful result.

## Request, security and resource policy

Each page or metadata operation has a **30-second total deadline**, including operation queueing, authority checks, SDK retry backoff and body processing. SDK standard mode permits at most two retries. A final transport guard independently caps physical sends at three; there is no application retry loop. Stale-connection retries and automatic clock-skew correction are disabled. Cancellation and session invalidation cancel in-flight requests; rejected/cancelled results do not advance the page cursor. Disposal waits for active work and releases the SDK client and owned transport.

Final outbound checks require the configured HTTPS authority/port (or explicit virtual-hosted bucket authority), expected signing region and GET/HEAD method. All 3xx responses fail closed, including regional redirects; automatic authenticated redirection is not implemented. Production uses normal certificate validation with no HTTP or TLS-bypass option. SDK raw logging, response logging, metrics and client-side monitoring handlers are disabled to avoid exporting provider data or identities. Public failures contain fixed categories, never raw SDK exceptions or response bodies. HTTP 403 is access denial, not an unsupported-feature claim.

Current explicit bounds:

| Resource | Bound / behavior |
| --- | --- |
| Requested and accepted page size | At most 1,000 entries |
| Traversal | At most 4,096 pages per listing instance; then PaginationLimit |
| Continuation tokens | Opaque; at most 16,384 characters, valid Unicode, nonblank, no control characters; SHA-256 history detects repeated tokens/cycles |
| Listing/error response body | At most 8 MiB before SDK parsing |
| Successful XML | No DTD/external resolver; depth at most 64, at most 65,536 child elements |
| User metadata | At most 128 entries and 65,536 total name/value characters |
| Retained listing data | One returned page per call; no accumulated row history in the adapter |

The finite traversal limit can constrain very large listings; it fails explicitly rather than silently truncating or looping. The future UI must retain its own bounded page cache. These are read-only limits and must not be reused as a write/recovery policy.

Virtual-hosted addressing currently accepts simple DNS bucket names without dots and requires a DNS endpoint. Dotted buckets and IP endpoints must use explicitly selected path-style addressing; unsupported virtual-hosted configurations fail before network access without automatic switching. ARNs, access points, acceleration, MRAP and S3 Express are outside this implementation. A wrong region fails rather than silently changing authority. System clock correctness is required.

## Executed validation

Fedora 44 x64, .NET SDK **10.0.113**, runtime **10.0.13**. `./eng/validate.sh` completed successfully with the repository telemetry opt-outs:

| Check | Result |
| --- | --- |
| Locked restore | PASS; eight project lockfiles |
| Release build | PASS; zero warnings, zero errors |
| Core tests | 34 passed |
| Platform tests | 38 passed; 1 skipped |
| Desktop headless tests | 10 passed |
| New S3 SDK tests | 52 passed |
| Total | **134 passed, 0 failed, 1 skipped** |
| NuGet audit, including transitives | No known vulnerabilities reported on 2026-10-22 |

All 82 Session 2 tests remain passing. Package versions are unchanged; the new S3 test project uses existing dependencies. The union remains 60 package/version pairs; see [dependency inventory](DEPENDENCIES.md).

Fixtures use actual SDK serialization, signing, retry handlers and response parsing with synthetic credentials. Coverage includes path/virtual addressing, explicit region/session-token signing, key and metadata semantics, ListBuckets denied while direct listing succeeds, opaque pagination/cycles/page caps, malformed XML and body bounds, 403/404/authentication classification, redirect rejection, transport errors, retry recovery/exhaustion, cancellation during requests/backoff, body-read deadlines and session replacement/disposal/revision checks. Deadline tests use a shorter injected internal deadline; the public factory sets 30 seconds. The test-side independent signature verifier is not production cryptography.

An ephemeral loopback TLS server with a disposable self-signed certificate exercised the real HTTP/TLS stack: validation failed and no HTTP credential bytes were received. No trust-store change or certificate-validation override was used. This establishes untrusted-certificate rejection, not enterprise CA, expiry, hostname-mismatch or actual provider qualification.

**Not executed:** live AWS/Ceph requests, existing AWS CLI credential access, native vault tests, graphical desktop execution, Windows execution or provisioning. The native-vault test skips without explicit opt-in. Linux native vault/desktop checks remain **BLOCKED** without a suitable interactive session; Windows native verification remains **BLOCKED** pending its VM. Headless success is not Windows compatibility evidence.

## Prerequisites and remaining limits

Session 4 requires separate approval. It can consume the controller and Core contracts for explicit connect/disconnect, bucket/prefix navigation and on-demand metadata. It must cancel superseded connection attempts (including any vault prompt), reject stale UI completions, show fixed error categories safely, render provider text as data, preserve exact keys behind display formatting and cap its page cache. The controller prevents stale opens from publishing a session; callers still own cancellation of a pending credential-resolution operation.

Subsequent live qualification needs a separately authorized, explicitly opted-in harness, identified AWS/Ceph builds/configurations and safe read-only targets. Verify actual encoded listings, bucket pagination support, HEAD/signature behavior, permissions, region behavior and network deadlines. Any private HTTP laboratory exception must remain test-only, outside the published Desktop graph; no such exception was added here. Offline fixtures establish no new Ceph compatibility claim. Enterprise proxies, interception and enterprise CA behavior remain unresolved requirements/qualification work.

Native vault/runtime qualification, Session 2 crash-orphan cleanup and Windows VM validation remain open. Future writes still require independent retry, overwrite and ambiguous-outcome review under ADR-006. No resources were provisioned, shared objects modified, commits created or changes pushed.
