using Amazon;
using Amazon.SimpleNotificationService;

namespace AwsEmulator;

public static class SnsEmulatorExtensions
{
    /// <summary>An SNS client config routed to <paramref name="emulator"/>.</summary>
    public static AmazonSimpleNotificationServiceConfig CreateSnsConfig(this EmbeddedAwsEmulator emulator, RegionEndpoint? region = null) =>
        emulator.Configure(new AmazonSimpleNotificationServiceConfig(), region);

    /// <summary>An SNS client served by <paramref name="emulator"/>.</summary>
    public static IAmazonSimpleNotificationService CreateSnsClient(this EmbeddedAwsEmulator emulator, RegionEndpoint? region = null) =>
        new AmazonSimpleNotificationServiceClient(EmbeddedAwsEmulator.Credentials, emulator.CreateSnsConfig(region));
}
