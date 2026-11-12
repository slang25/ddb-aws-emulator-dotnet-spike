using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace AwsEmulator.DynamoDb;

public static class DynamoDbClientExtensions
{
    /// <summary>
    /// Waits until <paramref name="tableName"/> is ACTIVE. Like real DynamoDB, the emulator reports a new
    /// table as CREATING briefly and refuses item operations on it until then; the emulator's
    /// DescribeTable answers once creation has settled, so this is normally a single call.
    /// </summary>
    public static async Task WaitForTableActiveAsync(this IAmazonDynamoDB ddb, string tableName, CancellationToken cancellationToken = default)
    {
        while ((await ddb.DescribeTableAsync(tableName, cancellationToken).ConfigureAwait(false)).Table.TableStatus != TableStatus.ACTIVE)
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
    }
}
