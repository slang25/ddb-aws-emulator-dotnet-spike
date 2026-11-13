using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace AwsEmulator;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that serves every request from an in-process aws-emulator
/// instance instead of the network. The request is handed to the Rust router over FFI and the
/// response comes back through a native callback that completes the returned task.
/// </summary>
public sealed class EmulatorHttpMessageHandler : HttpMessageHandler
{
    private readonly EmulatorSafeHandle _emulator;

    internal EmulatorHttpMessageHandler(EmulatorSafeHandle emulator) => _emulator = emulator;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_emulator.IsClosed, this);
        var uri = request.RequestUri ?? throw new InvalidOperationException("Request has no URI.");

        var body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        var headers = new StringBuilder();
        if (request.Headers.Host is null)
            headers.Append("host: ").Append(uri.Authority).Append("\r\n");
        foreach (var (name, values) in request.Headers)
            AppendHeader(headers, name, values);
        if (request.Content is not null)
        {
            foreach (var (name, values) in request.Content.Headers)
                AppendHeader(headers, name, values);
        }

        var pending = new PendingRequest(request);
        var handle = GCHandle.Alloc(pending);
        await using var registration = cancellationToken.Register(
            static state => ((PendingRequest)state!).Completion.TrySetCanceled(), pending);

        Send(request.Method.Method, uri.AbsoluteUri, headers.ToString(), body, GCHandle.ToIntPtr(handle));
        return await pending.Completion.Task.ConfigureAwait(false);
    }

    private static void AppendHeader(StringBuilder builder, string name, IEnumerable<string> values) =>
        builder.Append(name).Append(": ").AppendJoin(", ", values).Append("\r\n");

    private unsafe void Send(string method, string uri, string headers, byte[] body, nint context)
    {
        var methodBytes = Encoding.UTF8.GetBytes(method);
        var uriBytes = Encoding.UTF8.GetBytes(uri);
        var headerBytes = Encoding.UTF8.GetBytes(headers);
        fixed (byte* m = methodBytes)
        fixed (byte* u = uriBytes)
        fixed (byte* h = headerBytes)
        fixed (byte* b = body)
        {
            NativeMethods.EmulatorSend(
                _emulator,
                m, (nuint)methodBytes.Length,
                u, (nuint)uriBytes.Length,
                h, (nuint)headerBytes.Length,
                b, (nuint)body.Length,
                &OnComplete,
                context);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnComplete(nint context, ushort status, byte* headers, nuint headersLength, byte* body, nuint bodyLength)
    {
        var handle = GCHandle.FromIntPtr(context);
        var pending = (PendingRequest)handle.Target!;
        handle.Free();
        try
        {
            var bodyBytes = new ReadOnlySpan<byte>(body, checked((int)bodyLength)).ToArray();
            if (status == 0)
            {
                pending.Completion.TrySetException(new AwsEmulatorException(Encoding.UTF8.GetString(bodyBytes)));
                return;
            }

            var response = new HttpResponseMessage((HttpStatusCode)status)
            {
                RequestMessage = pending.Request,
                Content = new ByteArrayContent(bodyBytes),
            };
            var headerText = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(headers, checked((int)headersLength)));
            foreach (var line in headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var name = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                if (!response.Headers.TryAddWithoutValidation(name, value))
                    response.Content.Headers.TryAddWithoutValidation(name, value);
            }

            // Hand off to the thread pool: never run SDK continuations on the native worker thread.
            if (!pending.Completion.TrySetResult(response))
                response.Dispose();
        }
        catch (Exception ex)
        {
            // An exception must never unwind into native code.
            pending.Completion.TrySetException(ex);
        }
    }

    private sealed class PendingRequest(HttpRequestMessage request)
    {
        public HttpRequestMessage Request { get; } = request;

        public TaskCompletionSource<HttpResponseMessage> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>The emulator could not process a request at all (bad input, or a panic in the emulator).</summary>
public sealed class AwsEmulatorException(string message) : Exception(message);
