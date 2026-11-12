using Amazon;
using Amazon.DynamoDBv2;
using Amazon.Runtime;

namespace AwsEmulator.DynamoDb;

/// <summary>
/// An in-process DynamoDB, backed by aws-emulator's DynamoDB implementation compiled into a native
/// library. No socket, container, or Java: every SDK call is dispatched in memory.
/// </summary>
/// <remarks>
/// Each instance owns a fresh, empty engine. Clients created from one instance share its tables;
/// two instances never share state, so one emulator per test gives full isolation.
/// </remarks>
public sealed class DynamoDbEmulator : IDisposable
{
    /// <summary>Signature checking is off in the emulator, so any credentials work.</summary>
    public static AWSCredentials Credentials { get; } = new BasicAWSCredentials("emulator", "emulator");

    private readonly EmulatorSafeHandle _handle;

    public DynamoDbEmulator()
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

    /// <summary>A DynamoDB client config routed to this emulator.</summary>
    public AmazonDynamoDBConfig CreateConfig(RegionEndpoint? region = null) => new()
    {
        RegionEndpoint = region ?? RegionEndpoint.USEast1,
        HttpClientFactory = HttpClientFactory,
    };

    /// <summary>A DynamoDB client served by this emulator.</summary>
    public IAmazonDynamoDB CreateClient(RegionEndpoint? region = null) =>
        new AmazonDynamoDBClient(Credentials, CreateConfig(region));

    public void Dispose()
    {
        HttpMessageHandler.Dispose();
        _handle.Dispose();
    }
}
