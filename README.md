<img src="assets/icon.svg" width="64" height="64" alt="">

# ddb-aws-emulator-dotnet-spike

Spike: run the DynamoDB implementation from [aws/aws-emulator](https://github.com/aws/aws-emulator)
**in-process** under the AWS SDK for .NET. The SDK client keeps working normally, but its
`HttpClient` is swapped for one whose handler dispatches into the emulator in memory. There is
no socket, container, Java, or `ServiceURL`.

```csharp
using var emulator = new DynamoDbEmulator();          // fresh, empty, isolated engine (~60 ms)
using var ddb = emulator.CreateClient();              // a normal AmazonDynamoDBClient

await ddb.CreateTableAsync(...);                      // ACTIVE immediately (see "Patches")
await ddb.QueryAsync(...);                             // served in memory

// Or wire up any SDK config yourself:
var config = new AmazonDynamoDBConfig { HttpClientFactory = emulator.HttpClientFactory, ... };
```

## How it works

```
AmazonDynamoDBClient
  └─ HttpClientFactory  (EmulatorHttpClientFactory)
       └─ HttpClient → EmulatorHttpMessageHandler
            └─ P/Invoke ddb_emulator_send(method, uri, headers, body, callback, GCHandle)
                 └─ libaws_emulator_ddb_ffi.so  (Rust cdylib)
                      └─ tokio runtime → MultiServiceRouter::dispatch(http::Request)
                           └─ aws-emulator DynamoDB service crate + InMemoryEngine
```

- **`native/aws-emulator-ddb-ffi`** is a small Rust `cdylib`. aws-emulator's own embedded facade
  (`AwsEmulatorEmbedded`) builds a router over **all ~437 services**. This crate instead builds a
  `MultiServiceRouter` that registers only DynamoDB and the sibling crates it already depends on
  (Streams, Kinesis, S3, KMS, STS). It exposes a C ABI with three functions: `new`, `free`, and an
  async `send`. `send` copies the request, spawns `router.dispatch` on a tokio runtime with
  16 MB worker stacks (matching the emulator binary), and calls a completion callback.
- **`native/aws-emulator`** is a shallow git submodule of aws-emulator pinned to commit `7753983c`.
  The FFI crate path-depends on it. **`native/patches/*.patch`** are applied to it by
  `native/apply-patches.sh` (idempotent) before every native build.
- **`src/AwsEmulator.DynamoDb`** is the .NET library. `EmulatorHttpMessageHandler` serialises the
  `HttpRequestMessage`, passes an `[UnmanagedCallersOnly]` function pointer plus a `GCHandle` to a
  `TaskCompletionSource`, and builds the `HttpResponseMessage` in the callback. The callback never
  blocks a thread. The csproj runs `cargo build --release` before building (use
  `-p:SkipNativeBuild=true` to skip it) and copies the `.so` to every project that references it.
- **`samples/AwsEmulator.DynamoDb.Sample`** exercises CreateTable with a GSI, Put, Query with a
  key condition, filter and descending order, a GSI query, a conditional UpdateItem with `ADD`,
  TransactWriteItems, a paginated Scan, and `DynamoDBContext` (the object persistence model).
- **`tests/AwsEmulator.DynamoDb.Tests`** contains xUnit v3 tests. They cover attribute-type round
  trips, query semantics, typed SDK exceptions, per-emulator isolation, and 200 concurrent writes.

## Running

Prerequisites: a Rust toolchain, the .NET 10 SDK, and Linux x64. Clone with
`git clone --recurse-submodules`, or run `git submodule update --init` in an existing clone.
The first build compiles the emulator crates, which took about 8 minutes on 16 cores and leaves about 5 GB in
`native/aws-emulator-ddb-ffi/target`. After that, builds are incremental.

```bash
dotnet run --project samples/AwsEmulator.DynamoDb.Sample
dotnet test
```

## Findings

| | |
|---|---|
| Works? | Yes. Everything in the sample and tests behaves like DynamoDB, including validation errors (for example, table name length) and typed SDK exceptions. |
| Network | None. `strace -e connect` shows zero `connect()` calls. Telemetry is never initialised because we skip the embedded facade. |
| Startup | About 60 ms per emulator instance. |
| Latency | About 170 µs per `GetItem` round trip through the full SDK pipeline (Debug build). |
| Native lib size | 61 MB stripped (560 MB with debug info). |
| Cold build | About 8 min and 5 GB of `target/`, mostly DynamoDB's transitive service crates. |
| **Table creation takes 3 s (patched out)** | Upstream holds new tables in `CREATING` for a hardcoded `STATUS_TRANSITION = 3s` (`service-dynamodb/src/state.rs`). Item operations return `ResourceNotFoundException` until the table is `ACTIVE`. Nothing exposes it: no `EmbeddedOptions` field, CLI flag, env var, config key, or swappable clock. The same constant also gates UpdateTable, DeleteTable, TTL, backups, replicas, and resource policies. We patch it to `Duration::ZERO`; see "Patches". |

## Patches

| Patch | Why |
|---|---|
| `0001-dynamodb-instant-status-transitions.patch` | Sets DynamoDB's `STATUS_TRANSITION` from 3 s to zero, so table create, update, and delete and the other status transitions settle instantly. Upstream chose 3 s so callers can observe transient states, which costs every in-process test 3 s per table. DynamoDB Streams has its own `STREAM_ENABLE_DELAY`, which is left as is. **Trade-off:** transient states are never observable, so the CreateTable response itself says `ACTIVE` (real AWS says `CREATING`), and DeleteTable removes the table at once. Code that waits for `ACTIVE` is unaffected. Code that asserts on `CREATING` or `DELETING` will notice. |

To bump the emulator: check out a newer commit in `native/aws-emulator`, rerun
`native/apply-patches.sh` (it fails loudly if a patch no longer applies), and rebuild.

### Open questions / next steps

- Packaging: build the `.so`/`.dylib`/`.dll` per RID in CI and ship them in
  `runtimes/<rid>/native/` of a NuGet package. The README says aws-emulator source builds
  support Linux only. macOS and Windows would need validation.
- Ask upstream for an `EmbeddedOptions` switch for instant transitions, so the patch can be dropped.
- More services: the FFI crate could take a list of services to register. The same handler works
  for any SDK client, but each service adds build time.
- Streaming bodies: requests and responses are fully buffered. That is fine for DynamoDB, but S3
  would want streaming.
