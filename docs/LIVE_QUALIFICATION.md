# Explicitly opted-in read-only S3 qualification

`BucketBus.Integration.slnx` contains the separate `BucketBus.S3.IntegrationTests` project. Ordinary solution tests do not include it. To validate its offline guards without any live case, even when live environment variables happen to be set:

```bash
./eng/validate-integration.sh
```

Running the integration solution with opt-in absent skips its live case before configuration, credential resolution, client construction or network access. An opted-in run with invalid configuration fails with a fixed message; it never silently falls back to another credential source.

Create a local JSON file under ignored `docs/internal/`. All values below are fictional examples; select the specifically authorized target and named literal shared-credentials section. The configuration and evidence paths must be absolute, within this checkout's `docs/internal/`, without symlinks.

```json
{
  "Schema": 1,
  "Endpoint": "https://storage.example.test",
  "ApprovedEndpoint": "https://storage.example.test",
  "Region": "us-east-1",
  "Addressing": "Path",
  "Bucket": "explicit-approved-bucket",
  "CredentialProfile": "explicit-selected-profile",
  "DiscoverBuckets": false,
  "MaximumPages": 3,
  "HeadKeys": ["existing-object"],
  "EvidenceFile": "/absolute/checkout/docs/internal/live-results.json"
}
```

The harness reads only the named section in `~/.aws/credentials`, supporting literal access key/secret and optional session token. Process, role, SSO and other profile providers are rejected; no environment/default-profile/container/metadata resolution is attempted. Credential values are never command arguments or output. No credential files are written. Account discovery is separately selected; direct bucket listing runs first.

Supply `BUCKETBUS_APPROVED_ENDPOINT` independently from the operator's approval,
never by copying/reading either endpoint field out of the target JSON. Missing,
unsafe or mismatched approval fails before credential resolution/client creation.
Both approval and target must be HTTPS authorities; canonical host/default-port
forms are compared. The JSON `ApprovedEndpoint` field remains only a consistency
check, not authorization. Changing both JSON fields cannot override the external
approval. This guards configuration mistakes, not a malicious operator who can
change both the process environment and the file.

After selecting the separately approved authority and explicit local file
(the endpoint below is fictional, not a usable laboratory authorization):

```bash
BUCKETBUS_RUN_LIVE_TESTS=1 \
BUCKETBUS_APPROVED_ENDPOINT='https://storage.example.test' \
BUCKETBUS_LIVE_CONFIG="$(pwd)/docs/internal/live.json" \
  dotnet test BucketBus.Integration.slnx -c Release --no-build --no-restore --filter Category=LiveS3
```

HTTPS validation is normal; redirects are disabled. The harness bypasses environment proxies to reach the explicitly selected target directly. It uses the production adapter with GET/HEAD only, at most three physical sends / 30 seconds per operation, at most eight pages and twenty configured metadata keys, and a six-minute suite cancellation budget. It never downloads object bodies or mutates resources. A result file records fixed status categories without object keys, credentials, response bodies or raw exceptions. Missing optional coverage is explicitly reported; a successful test case does not mean every qualification scenario passed.

Session 5 separately used authorized disposable write tooling to create pagination/exact-key fixtures, followed by this unchanged read-only suite and manifest-based cleanup. That tooling and its environment-specific manifest remain private. It is not an application transfer implementation or an automatic setup/cleanup hook in the read-only harness. Future write experiments need their own explicit opt-in, exact target, ownership manifest, bounded operations and recorded uncertain/cleanup outcomes. Laboratory authority does not authorize cluster, network or VM teardown.

## Single-file write qualification

Phase 1B.2 adds a **separate** `TransferQualificationTests` entry point. The read-only
harness above retains its original GET/HEAD transport and opt-in. Ordinary tests and
`eng/validate-integration.sh` never run either live entry point.

Only after approval of a disposable scope, create a private JSON configuration:

```json
{
  "Connection": {
    "Schema": 1,
    "Endpoint": "https://storage.example.test",
    "ApprovedEndpoint": "https://storage.example.test",
    "Region": "us-east-1",
    "Addressing": "Path",
    "Bucket": "explicit-transfer-test-bucket",
    "CredentialProfile": "explicit-selected-profile",
    "DiscoverBuckets": false,
    "MaximumPages": 1,
    "HeadKeys": [],
    "EvidenceFile": "/absolute/checkout/docs/internal/transfer-evidence.json"
  },
  "Prefix": "qualification/",
  "ManifestFile": "/absolute/checkout/docs/internal/transfer-manifest.json",
  "QualificationFile": "/absolute/checkout/docs/internal/transfer-qualification-results.json"
}
```

The example is fictional and conveys no live authorization. Independently supply
both the approved HTTPS authority and exact bucket/prefix, before credentials or
network access:

```bash
BUCKETBUS_RUN_WRITE_TESTS=1 \
BUCKETBUS_APPROVED_ENDPOINT='https://storage.example.test' \
BUCKETBUS_APPROVED_WRITE_SCOPE='explicit-transfer-test-bucket/qualification/' \
BUCKETBUS_WRITE_CONFIG="$(pwd)/docs/internal/transfer-config.json" \
  dotnet test BucketBus.Integration.slnx -c Release --no-build --no-restore \
    --filter 'FullyQualifiedName~TransferQualificationTests'
```

This explicitly mutating test may create the approved dedicated bucket if absent.
It uses the actual S3 transfer and native filesystem adapters for zero-byte, Unicode
and random-content transfers, verifies exact bytes, tries a conflicting PUT with
different bytes, verifies that the original object remains unchanged, tests download
cancellation and a missing-object read. Fixture names are random beneath the approved
prefix. The private manifest records intended keys/hashes before upload and actual
operation identities before sending. Cleanup requires the recorded operation identity,
full hash and matching ETag/version; unknown/mismatched objects are retained and the
suite fails visibly. Confirmed deletion is followed by an absence check. The bucket
is retained; no infrastructure is torn down. Keep manifests from each run for review.

Successful round-trip/collision checks produce a private compatibility report at
`QualificationFile` (the existing test-configuration field name). This is test evidence
only: the application never reads or installs it, and it does not enable uploads or
expire normal users' access. Retest after provider changes before making compatibility
claims. The independent opt-in, endpoint/scope approvals and manifest guards above
remain mandatory for every live run, regardless of prior test reports.
No credentials belong in these JSON files. A crash may leave manifest-owned fixtures;
inspect them explicitly instead of blindly repeating cleanup. The five-minute live
suite budget and bounded cleanup do not establish long-transfer/WAN performance.
