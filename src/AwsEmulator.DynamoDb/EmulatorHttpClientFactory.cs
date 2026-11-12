using Amazon.Runtime;

namespace AwsEmulator.DynamoDb;

/// <summary>
/// Hands the AWS SDK <see cref="HttpClient"/>s that route through the in-process emulator.
/// Assign it to <see cref="ClientConfig.HttpClientFactory"/>.
/// </summary>
internal sealed class EmulatorHttpClientFactory(HttpMessageHandler handler) : HttpClientFactory
{
    public override HttpClient CreateHttpClient(IClientConfig clientConfig) =>
        new(handler, disposeHandler: false)
        {
            Timeout = clientConfig.Timeout ?? TimeSpan.FromSeconds(100),
        };

    // The SDK caches HttpClients per unique string; each emulator must get its own.
    public override string GetConfigUniqueString(IClientConfig clientConfig) =>
        $"AwsEmulator:{RuntimeHelpersId(handler)}";

    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;

    private static int RuntimeHelpersId(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
}
