//! C ABI over an in-process aws-emulator router serving DynamoDB, SQS, SNS, EventBridge, S3 and
//! the services they call into.
//!
//! The .NET side (`AwsEmulator.Core`) P/Invokes these functions from an
//! `HttpMessageHandler`, so the AWS SDK for .NET talks to the emulator with no socket.
//!
//! Lifecycle: `aws_emulator_new` → many `aws_emulator_send` → `aws_emulator_free`.
//! Each emulator owns a fresh in-memory engine; two emulators never share state.
//!
//! `aws_emulator_send` is asynchronous: it copies the request, returns at once, and later
//! invokes `callback` exactly once on a runtime worker thread. The response buffers handed to
//! the callback are only valid for the duration of the callback; the caller must copy them.

use std::ffi::c_void;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::{Arc, OnceLock};

use engine::InMemoryEngine;
use smithy_server_runtime::service::Service;
use smithy_server_runtime::MultiServiceRouter;

/// The emulator binary runs its workers on 16 MiB stacks; operation handlers are deep.
const WORKER_STACK_SIZE: usize = 16 * 1024 * 1024;

fn runtime() -> &'static tokio::runtime::Runtime {
    static RUNTIME: OnceLock<tokio::runtime::Runtime> = OnceLock::new();
    RUNTIME.get_or_init(|| {
        tokio::runtime::Builder::new_multi_thread()
            .thread_name("aws-emulator")
            .thread_stack_size(WORKER_STACK_SIZE)
            .enable_all()
            .build()
            .expect("failed to build tokio runtime")
    })
}

pub struct Emulator {
    router: MultiServiceRouter,
}

/// Completion callback: `(context, status, headers, headers_len, body, body_len)`.
///
/// `headers` is UTF-8 `name: value\r\n` lines. Status 0 means the request never reached the
/// emulator (bad input or a panic); `body` then holds a UTF-8 error message.
pub type SendCallback = extern "C" fn(
    context: *mut c_void,
    status: u16,
    headers: *const u8,
    headers_len: usize,
    body: *const u8,
    body_len: usize,
);

/// Register one service. `#[inline(never)]` keeps each service's (large) constructor frame
/// separate instead of letting them coalesce into one huge frame, as upstream's router does.
#[inline(never)]
fn register<S: Service>(
    router: &mut MultiServiceRouter,
    engine: &Arc<InMemoryEngine>,
    new: fn(Arc<InMemoryEngine>) -> S,
) {
    router.register_service(new(engine.clone()));
}

/// Every service crate this library links (see Cargo.toml), all on one engine so cross-service
/// calls (SNS -> SQS, EventBridge -> targets, S3 -> notifications, ...) resolve in-process.
fn build_router(engine: Arc<InMemoryEngine>) -> MultiServiceRouter {
    let mut router = MultiServiceRouter::new();
    register(&mut router, &engine, service_com_amazonaws_cloudhsmv2_baldrapiservice::EmulatedService::new);
    register(&mut router, &engine, service_com_amazonaws_cloudwatch_graniteserviceversion20100801::EmulatedService::new);
    register(&mut router, &engine, service_com_amazonaws_cloudwatchlogs_logs_20140328::EmulatedService::new);
    register(&mut router, &engine, service_com_amazonaws_cognitoidentityprovider_awscognitoidentityproviderservice::EmulatedService::new);
    register(&mut router, &engine, service_com_amazonaws_dynamodb_dynamodb_20120810::EmulatedService::new);
    register(&mut router, &engine, service_com_amazonaws_dynamodbstreams_dynamodbstreams_20120810::EmulatedService::new);
    register(&mut router, &engine, service_com_amazonaws_ecr_amazonec2containerregistry_v20150921::EmulatedService::new);
    register(&mut router, &engine, service_com_amazonaws_eventbridge_awsevents::EmulatedService::new);
    register(&mut router, &engine, service_com_amazonaws_glue_awsglue::EmulatedService::new);
    register(&mut router, &engine, service_com_amazonaws_iam_awsidentitymanagementv20100508::EmulatedService::new);
    register(&mut router, &engine, service_com_amazonaws_kinesis_kinesis_20131202::EmulatedService::new);
    register(&mut router, &engine, service_com_amazonaws_kms_trentservice::EmulatedService::new);
    register(&mut router, &engine, service_com_amazonaws_lambda_awsgirapiservice::EmulatedService::new);
    register(&mut router, &engine, service_com_amazonaws_s3_amazons3::EmulatedService::new);
    register(&mut router, &engine, service_com_amazonaws_s3tables_s3tablebuckets::EmulatedService::new);
    register(&mut router, &engine, service_com_amazonaws_signer_wallabyservice::EmulatedService::new);
    register(&mut router, &engine, service_com_amazonaws_sns_amazonsimplenotificationservice::EmulatedService::new);
    register(&mut router, &engine, service_com_amazonaws_sqs_amazonsqs::EmulatedService::new);
    register(&mut router, &engine, service_com_amazonaws_ssoadmin_swbexternalservice::EmulatedService::new);
    register(&mut router, &engine, service_com_amazonaws_sts_awssecuritytokenservicev20110615::EmulatedService::new);
    register(&mut router, &engine, service_com_amazonaws_wafv2_awswaf_20190729::EmulatedService::new);
    // Signature checks off: the SDK can sign with any dummy credentials.
    router.set_request_authentication(false);
    router
}

/// Create an emulator over a fresh in-memory engine. Returns null on failure.
#[no_mangle]
pub extern "C" fn aws_emulator_new() -> *mut Emulator {
    let result = catch_unwind(|| {
        // Build on a runtime worker: service construction may expect a tokio context and a
        // big stack.
        runtime().block_on(async {
            tokio::spawn(async { build_router(Arc::new(InMemoryEngine::new())) })
                .await
                .expect("router construction panicked")
        })
    });
    match result {
        Ok(router) => Box::into_raw(Box::new(Emulator { router })),
        Err(_) => std::ptr::null_mut(),
    }
}

/// Free an emulator. In-flight requests keep their own clone of the router and finish normally.
///
/// # Safety
/// `emulator` must come from `aws_emulator_new` and not be used afterwards.
#[no_mangle]
pub unsafe extern "C" fn aws_emulator_free(emulator: *mut Emulator) {
    if !emulator.is_null() {
        drop(Box::from_raw(emulator));
    }
}

unsafe fn slice<'a>(ptr: *const u8, len: usize) -> &'a [u8] {
    if ptr.is_null() || len == 0 {
        &[]
    } else {
        std::slice::from_raw_parts(ptr, len)
    }
}

fn build_request(
    method: &[u8],
    uri: &[u8],
    headers: &[u8],
    body: Vec<u8>,
) -> Result<http::Request<bytes::Bytes>, String> {
    let mut builder = http::Request::builder()
        .method(http::Method::from_bytes(method).map_err(|e| format!("bad method: {e}"))?)
        .uri(http::Uri::try_from(uri).map_err(|e| format!("bad uri: {e}"))?);
    let headers = std::str::from_utf8(headers).map_err(|e| format!("headers not utf-8: {e}"))?;
    for line in headers.split("\r\n").filter(|l| !l.is_empty()) {
        let (name, value) = line
            .split_once(':')
            .ok_or_else(|| format!("malformed header line: {line}"))?;
        builder = builder.header(name.trim(), value.trim());
    }
    builder
        .body(bytes::Bytes::from(body))
        .map_err(|e| format!("bad request: {e}"))
}

struct Context(*mut c_void);
// The context is an opaque handle owned by the caller (a GCHandle on the .NET side).
unsafe impl Send for Context {}

fn fail(callback: SendCallback, context: *mut c_void, message: &str) {
    callback(context, 0, std::ptr::null(), 0, message.as_ptr(), message.len());
}

/// Dispatch one HTTP request to the emulator. Copies all inputs before returning, then calls
/// `callback(context, ...)` exactly once, possibly on another thread.
///
/// # Safety
/// `emulator` must be live; each `(ptr, len)` pair must describe readable memory (or be null/0).
#[no_mangle]
pub unsafe extern "C" fn aws_emulator_send(
    emulator: *const Emulator,
    method: *const u8,
    method_len: usize,
    uri: *const u8,
    uri_len: usize,
    headers: *const u8,
    headers_len: usize,
    body: *const u8,
    body_len: usize,
    callback: SendCallback,
    context: *mut c_void,
) {
    if emulator.is_null() {
        fail(callback, context, "emulator is null");
        return;
    }
    let router = (*emulator).router.clone();
    let request = build_request(
        slice(method, method_len),
        slice(uri, uri_len),
        slice(headers, headers_len),
        slice(body, body_len).to_vec(),
    );
    let request = match request {
        Ok(r) => r,
        Err(message) => {
            fail(callback, context, &message);
            return;
        }
    };
    let context = Context(context);
    runtime().spawn(async move {
        let context = context;
        let outcome = AssertUnwindSafe(router.dispatch(request));
        let outcome = futures_catch_unwind(outcome).await;
        match outcome {
            Ok(response) => {
                let mut header_text = String::new();
                for (name, value) in response.headers() {
                    header_text.push_str(name.as_str());
                    header_text.push_str(": ");
                    header_text.push_str(&String::from_utf8_lossy(value.as_bytes()));
                    header_text.push_str("\r\n");
                }
                let body = response.body();
                callback(
                    context.0,
                    response.status().as_u16(),
                    header_text.as_ptr(),
                    header_text.len(),
                    body.as_ptr(),
                    body.len(),
                );
            }
            Err(message) => fail(callback, context.0, &message),
        }
    });
}

/// Minimal `catch_unwind` for a future, so a handler panic still completes the caller's task.
async fn futures_catch_unwind<F: std::future::Future>(
    future: AssertUnwindSafe<F>,
) -> Result<F::Output, String> {
    let mut future = std::pin::pin!(future.0);
    std::future::poll_fn(move |cx| {
        match catch_unwind(AssertUnwindSafe(|| future.as_mut().poll(cx))) {
            Ok(std::task::Poll::Ready(v)) => std::task::Poll::Ready(Ok(v)),
            Ok(std::task::Poll::Pending) => std::task::Poll::Pending,
            Err(panic) => {
                let message = panic
                    .downcast_ref::<&str>()
                    .map(|s| s.to_string())
                    .or_else(|| panic.downcast_ref::<String>().cloned())
                    .unwrap_or_else(|| "emulator panicked".to_string());
                std::task::Poll::Ready(Err(format!("emulator panicked: {message}")))
            }
        }
    })
    .await
}
