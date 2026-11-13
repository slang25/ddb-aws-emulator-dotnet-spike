using Amazon;
using Amazon.SQS;

namespace AwsEmulator;

public static class SqsEmulatorExtensions
{
    /// <summary>An SQS client config routed to <paramref name="emulator"/>.</summary>
    public static AmazonSQSConfig CreateSqsConfig(this EmbeddedAwsEmulator emulator, RegionEndpoint? region = null) =>
        emulator.Configure(new AmazonSQSConfig(), region);

    /// <summary>An SQS client served by <paramref name="emulator"/>.</summary>
    public static IAmazonSQS CreateSqsClient(this EmbeddedAwsEmulator emulator, RegionEndpoint? region = null) =>
        new AmazonSQSClient(EmbeddedAwsEmulator.Credentials, emulator.CreateSqsConfig(region));
}
