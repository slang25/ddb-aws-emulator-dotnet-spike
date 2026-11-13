using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace AwsEmulator;

public static class DynamoDbEmulatorExtensions
{
    /// <summary>A DynamoDB client config routed to <paramref name="emulator"/>.</summary>
    public static AmazonDynamoDBConfig CreateDynamoDbConfig(this EmbeddedAwsEmulator emulator, RegionEndpoint? region = null) =>
        emulator.Configure(new AmazonDynamoDBConfig(), region);

    /// <summary>A DynamoDB client served by <paramref name="emulator"/>.</summary>
    public static IAmazonDynamoDB CreateDynamoDbClient(this EmbeddedAwsEmulator emulator, RegionEndpoint? region = null) =>
        new AmazonDynamoDBClient(EmbeddedAwsEmulator.Credentials, emulator.CreateDynamoDbConfig(region));

    /// <summary>
    /// Waits until <paramref name="tableName"/> is ACTIVE. With this repo's patched emulator a new
    /// table is ACTIVE at once, so this returns after one DescribeTable; it also works against real AWS.
    /// </summary>
    public static async Task WaitForTableActiveAsync(this IAmazonDynamoDB ddb, string tableName, CancellationToken cancellationToken = default)
    {
        while ((await ddb.DescribeTableAsync(tableName, cancellationToken).ConfigureAwait(false)).Table.TableStatus != TableStatus.ACTIVE)
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
    }
}
