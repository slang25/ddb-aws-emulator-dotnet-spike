using Amazon;
using Amazon.EventBridge;

namespace AwsEmulator;

public static class EventBridgeEmulatorExtensions
{
    /// <summary>An EventBridge client config routed to <paramref name="emulator"/>.</summary>
    public static AmazonEventBridgeConfig CreateEventBridgeConfig(this EmbeddedAwsEmulator emulator, RegionEndpoint? region = null) =>
        emulator.Configure(new AmazonEventBridgeConfig(), region);

    /// <summary>An EventBridge client served by <paramref name="emulator"/>.</summary>
    public static IAmazonEventBridge CreateEventBridgeClient(this EmbeddedAwsEmulator emulator, RegionEndpoint? region = null) =>
        new AmazonEventBridgeClient(EmbeddedAwsEmulator.Credentials, emulator.CreateEventBridgeConfig(region));
}
