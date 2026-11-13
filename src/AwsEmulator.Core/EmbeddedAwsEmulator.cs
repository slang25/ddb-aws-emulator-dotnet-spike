using Amazon;
using Amazon.Runtime;

namespace AwsEmulator;

/// <summary>
/// An in-process AWS, backed by aws-emulator compiled into a native library. No socket, container,
/// or endpoint URL: every SDK call is dispatched in memory.
/// </summary>
/// <remarks>
/// <para>
/// Each instance owns a fresh, empty engine shared by every service it serves (DynamoDB, SQS, SNS,
/// EventBridge, S3, and the services they call into), so cross-service features such as SNS → SQS
/// delivery or EventBridge rules work. Two instances never share state, so one emulator per test
/// gives full isolation.
/// </para>
/// <para>
/// Point any AWS SDK client at it with <see cref="Configure{TConfig}"/>, or use the per-service
/// <c>CreateXxxClient()</c> extension methods from the <c>AwsEmulator.*</c> service libraries.
/// </para>
/// </remarks>
public sealed class EmbeddedAwsEmulator : IDisposable
{
    /// <summary>Signature checking is off in the emulator, so any credentials work.</summary>
    public static AWSCredentials Credentials { get; } = new BasicAWSCredentials("emulator", "emulator");

    private readonly EmulatorSafeHandle _handle;

    public EmbeddedAwsEmulator()
    {
        _handle = NativeMethods.EmulatorNew();
        if (_handle.IsInvalid)
            throw new AwsEmulatorException("Failed to create the in-process emulator.");
        HttpMessageHandler = new EmulatorHttpMessageHandler(_handle);
        HttpClientFactory = new EmulatorHttpClientFactory(HttpMessageHandler);
    }

    /// <summary>A handler that serves requests from this emulator, for wiring up HttpClients yourself.</summary>
    public HttpMessageHandler HttpMessageHandler { get; }

    /// <summary>Plug into any AWS SDK <see cref="ClientConfig.HttpClientFactory"/>.</summary>
    public HttpClientFactory HttpClientFactory { get; }

    /// <summary>Routes an AWS SDK client config to this emulator, in <paramref name="region"/> (default us-east-1).</summary>
    public TConfig Configure<TConfig>(TConfig config, RegionEndpoint? region = null) where TConfig : ClientConfig
    {
        // Never read config.RegionEndpoint here: when unset, its getter runs the SDK's region
        // fallback chain, which ends in an EC2 instance-metadata (IMDS) call that hangs ~5s per
        // attempt off EC2.
        config.RegionEndpoint = region ?? RegionEndpoint.USEast1;
        config.HttpClientFactory = HttpClientFactory;
        return config;
    }

    public void Dispose()
    {
        HttpMessageHandler.Dispose();
        _handle.Dispose();
    }
}
