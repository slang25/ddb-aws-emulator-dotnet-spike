<img src="assets/icon.svg" width="64" height="64" alt="">

# ddb-aws-emulator-dotnet-spike

Spike: run [aws/aws-emulator](https://github.com/aws/aws-emulator) **in-process** under the AWS SDK
for .NET, for **DynamoDB, SQS, SNS, EventBridge and S3**. The SDK clients keep working normally,
but their `HttpClient` is swapped for one whose handler dispatches into the emulator in memory.
There is no socket, container, Java, or `ServiceURL`.

```csharp
using var emulator = new EmbeddedAwsEmulator();       // fresh, empty, isolated (~35-80 ms)

using var ddb = emulator.CreateDynamoDbClient();       // AwsEmulator.DynamoDb
using var sqs = emulator.CreateSqsClient();            // AwsEmulator.Sqs
using var sns = emulator.CreateSnsClient();            // AwsEmulator.Sns
using var events = emulator.CreateEventBridgeClient(); // AwsEmulator.EventBridge
using var s3 = emulator.CreateS3Client();              // AwsEmulator.S3

// Or route any SDK client config yourself (AwsEmulator.Core):
var config = emulator.Configure(new AmazonSQSConfig());
```

Every client from one emulator shares one in-memory engine, so **cross-service features work**:
SNS → SQS subscriptions (with filter policies and raw delivery), EventBridge rules → SQS/SNS
targets, and S3 event notifications → SQS. The sample chains EventBridge → SNS → two SQS queues.
Two emulators never share state, so one emulator per test gives full isolation.

## Layout

| Path | What |
|---|---|
| `native/aws-emulator` | Shallow git submodule of aws-emulator, pinned to `67fd1536` (v1.0.342). |
| `native/patches/*.patch` | Applied to the submodule by `native/apply-patches.sh` (idempotent) before every native build. |
| `native/aws-emulator-ffi` | Rust `cdylib` with a C ABI (`aws_emulator_new`, `aws_emulator_free`, and an async `aws_emulator_send`). |
| `src/AwsEmulator.Core` | `EmbeddedAwsEmulator`, the `HttpMessageHandler`/`HttpClientFactory`, and native interop. It builds the Rust library and ships the `.so`. Depends only on `AWSSDK.Core`. |
| `src/AwsEmulator.{DynamoDb,Sqs,Sns,EventBridge,S3}` | Thin per-service libraries. Each adds `CreateXxxClient()` / `CreateXxxConfig()` extensions and references its `AWSSDK.*` package. |
| `samples/AwsEmulator.Sample` | A DynamoDB walkthrough, SQS, S3, and the EventBridge → SNS → SQS fan-out. |
| `tests/AwsEmulator.Tests` | 19 xUnit v3 tests across all five services, including the cross-service flows. |

## How it works

```
Amazon*Client (any service)
  └─ ClientConfig.HttpClientFactory  (EmulatorHttpClientFactory)
       └─ HttpClient → EmulatorHttpMessageHandler
            └─ P/Invoke aws_emulator_send(method, uri, headers, body, callback, GCHandle)
                 └─ libaws_emulator_ffi.so  (Rust cdylib)
                      └─ tokio runtime → MultiServiceRouter::dispatch(http::Request)
                           └─ 21 aws-emulator service crates on one InMemoryEngine
```

- **One native library, one engine.** The five requested services call each other: SNS delivers
  to SQS, EventBridge targets SNS and SQS, and S3 notifies EventBridge and SQS. Separate
  per-service native libraries would duplicate the shared crates and could not deliver across
  services. The router therefore registers the requested services plus their transitive
  dependency closure, which is 21 crates in all (IAM, STS, KMS, Lambda, Kinesis, CloudWatch, Logs,
  Glue, …). They are compiled in either way, and registering them lets in-process cross-service
  calls resolve. To regenerate the list, run `cargo metadata` on the crate and keep `Cargo.toml`
  in sync with `build_router` in `src/lib.rs`.
- **The router is ours, not upstream's.** Upstream's `AwsEmulatorEmbedded` registers all ~437
  services, which means about 100 GB of build. We build a `MultiServiceRouter` directly instead.
  Signature checking is off, and upstream telemetry is never initialised.
- **The handler.** `EmulatorHttpMessageHandler` serialises the `HttpRequestMessage` and passes an
  `[UnmanagedCallersOnly]` callback plus a `GCHandle` to a `TaskCompletionSource`. The response is
  built in the callback, so no thread blocks. Rust runs requests on a tokio runtime with 16 MB
  worker stacks, matching the emulator binary.
- **`Cargo.lock`** is upstream's lockfile, pruned by cargo. Resolving fresh pulled a newer
  transitive dependency, and `rustpython-stdlib` (via Lambda) then failed to compile. When you
  bump the submodule, copy upstream's `Cargo.lock` again.

## Running

Prerequisites: a Rust toolchain, the .NET 10 SDK, and Linux x64. Clone with
`git clone --recurse-submodules`, or run `git submodule update --init` in an existing clone.

```bash
dotnet run --project samples/AwsEmulator.Sample
dotnet test
```

The first `dotnet build` runs `cargo build --release`, which takes about 5 to 10 minutes on 16 cores
and needs several GB of `native/aws-emulator-ffi/target`. After that, builds are incremental. Pass
`-p:SkipNativeBuild=true` to skip the cargo step.

## Findings

| | |
|---|---|
| Works? | Yes for all five services. That covers CRUD, typed SDK exceptions (`ConditionalCheckFailedException`, `QueueDoesNotExistException`, `NoSuchKeyException`, …), FIFO ordering and dedup, S3 multipart via `TransferUtility`, and every cross-service flow above. |
| Network | None. `strace -e connect` shows zero non-Unix `connect()` calls across the tests and the sample. |
| Speed | Emulator startup 35–80 ms. DynamoDB `GetItem` about 160 µs per round trip (Debug). All 19 tests take 2.5 s, and the sample takes 0.7 s end to end. |
| Native lib size | **250 MB** stripped with 21 services. It was 85 MB for DynamoDB's closure alone and 61 MB before DynamoDB pulled in CloudWatch. |
| **SDK region gotcha** | Never *read* `ClientConfig.RegionEndpoint` before setting it. When it is unset, the getter runs the SDK's region fallback chain, which ends in an EC2 instance-metadata (IMDS, `169.254.169.254`) call. Off EC2 that call hangs about 5 s per attempt, three attempts per client. An early version of `Configure` did this: each test took 15 s and the code reached the network. `Configure` now only assigns the region. |
| Hard-coded delays | Upstream models AWS timings with constants that have no off switch. DynamoDB's 3 s table status transition is patched out (see below). The rest are left as they are because they mirror real AWS rules and are off the common path: SQS's 5 min FIFO dedup window, 60 s between `PurgeQueue` calls, and 0.5 s before a deleted queue name can be reused; SNS's 5 min FIFO dedup; EventBridge's 3 s first scheduled-rule firing; and S3's 3 s replication settle. |
| Test-writing note | To prove a message *didn't* arrive (filter policies, non-matching rules), a test must wait out an empty long poll. Those tests take about 2 s each because of `WaitTimeSeconds = 2`. |

## Patches

| Patch | Why |
|---|---|
| `0001-dynamodb-instant-status-transitions.patch` | Sets DynamoDB's `STATUS_TRANSITION` from 3 s to zero, so table create, update, and delete and the other status transitions settle instantly. Upstream chose 3 s so callers can observe transient states, which costs every in-process test 3 s per table. Nothing exposes it: no `EmbeddedOptions` field, CLI flag, env var, config key, or swappable clock. **Trade-off:** transient states are never observable, so the CreateTable response itself says `ACTIVE` (real AWS says `CREATING`), and DeleteTable removes the table at once. Code that waits for `ACTIVE` is unaffected. Code that asserts on `CREATING` or `DELETING` will notice. |

To bump the emulator: check out a newer commit in `native/aws-emulator`, copy its `Cargo.lock`
into `native/aws-emulator-ffi/`, rerun `native/apply-patches.sh` (it fails loudly if a patch no
longer applies), check whether the service dependency closure changed, and rebuild.

## Open questions / next steps

- **Size versus selection.** 250 MB is the price of the full dependency closure in one library.
  Cargo features could let a consumer build only the services they need, but cross-service flows
  need their targets compiled in, so the closure stays large for SNS, EventBridge, and S3.
- **Packaging.** Build the `.so`/`.dylib`/`.dll` per RID in CI and ship them in
  `runtimes/<rid>/native/` of an `AwsEmulator.Core` NuGet package, with the per-service packages
  depending on it. aws-emulator only supports Linux source builds, so macOS and Windows need
  validation.
- **Upstream asks.** An `EmbeddedOptions` switch for instant transitions would let us drop the
  patch.
- **Streaming bodies.** Requests and responses are fully buffered. A 12 MB multipart upload is
  fine, but very large S3 objects would want streaming across the FFI.
