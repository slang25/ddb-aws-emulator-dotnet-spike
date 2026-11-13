using Amazon;
using Amazon.S3;

namespace AwsEmulator;

public static class S3EmulatorExtensions
{
    /// <summary>An S3 client config routed to <paramref name="emulator"/>.</summary>
    public static AmazonS3Config CreateS3Config(this EmbeddedAwsEmulator emulator, RegionEndpoint? region = null) =>
        emulator.Configure(new AmazonS3Config(), region);

    /// <summary>An S3 client served by <paramref name="emulator"/>.</summary>
    public static IAmazonS3 CreateS3Client(this EmbeddedAwsEmulator emulator, RegionEndpoint? region = null) =>
        new AmazonS3Client(EmbeddedAwsEmulator.Credentials, emulator.CreateS3Config(region));
}
