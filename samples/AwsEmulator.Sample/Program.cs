using System.Diagnostics;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.Model;
using Amazon.EventBridge.Model;
using Amazon.S3.Model;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using AwsEmulator;

// Everything below runs against an in-process DynamoDB: no network, no container.
var startup = Stopwatch.StartNew();
using var emulator = new EmbeddedAwsEmulator();
Console.WriteLine($"Emulator up in {startup.ElapsedMilliseconds} ms");

Console.WriteLine("\n== DynamoDB");
using var ddb = emulator.CreateDynamoDbClient();

const string table = "Orders";

await ddb.CreateTableAsync(new CreateTableRequest
{
    TableName = table,
    BillingMode = BillingMode.PAY_PER_REQUEST,
    AttributeDefinitions =
    [
        new("PK", ScalarAttributeType.S),
        new("SK", ScalarAttributeType.S),
        new("Status", ScalarAttributeType.S),
    ],
    KeySchema = [new("PK", KeyType.HASH), new("SK", KeyType.RANGE)],
    GlobalSecondaryIndexes =
    [
        new GlobalSecondaryIndex
        {
            IndexName = "ByStatus",
            KeySchema = [new("Status", KeyType.HASH), new("SK", KeyType.RANGE)],
            Projection = new Projection { ProjectionType = ProjectionType.ALL },
        },
    ],
});
var described = await ddb.DescribeTableAsync(table);
Console.WriteLine($"Created table {table}: {described.Table.TableStatus}, GSIs: {string.Join(",", described.Table.GlobalSecondaryIndexes.Select(g => g.IndexName))}");

// Seed: 3 customers x 4 orders.
var statuses = new[] { "PENDING", "SHIPPED" };
foreach (var customer in new[] { "alice", "bob", "carol" })
for (var i = 1; i <= 4; i++)
{
    await ddb.PutItemAsync(table, new Dictionary<string, AttributeValue>
    {
        ["PK"] = new($"CUSTOMER#{customer}"),
        ["SK"] = new($"ORDER#{i:D3}"),
        ["Status"] = new(statuses[i % 2]),
        ["Total"] = new AttributeValue { N = (i * 10.5m).ToString() },
    });
}
Console.WriteLine("Seeded 12 orders");

// Query with key condition + filter expression.
var query = await ddb.QueryAsync(new QueryRequest
{
    TableName = table,
    KeyConditionExpression = "PK = :pk AND begins_with(SK, :prefix)",
    FilterExpression = "#total > :min",
    ExpressionAttributeNames = new() { ["#total"] = "Total" },
    ExpressionAttributeValues = new()
    {
        [":pk"] = new("CUSTOMER#alice"),
        [":prefix"] = new("ORDER#"),
        [":min"] = new AttributeValue { N = "15" },
    },
    ScanIndexForward = false,
});
Console.WriteLine($"Query alice orders with Total > 15 (desc): {string.Join(", ", query.Items.Select(i => $"{i["SK"].S}={i["Total"].N}"))}");

// Query the GSI.
var shipped = await ddb.QueryAsync(new QueryRequest
{
    TableName = table,
    IndexName = "ByStatus",
    KeyConditionExpression = "#s = :s",
    ExpressionAttributeNames = new() { ["#s"] = "Status" },
    ExpressionAttributeValues = new() { [":s"] = new("SHIPPED") },
});
Console.WriteLine($"GSI ByStatus=SHIPPED: {shipped.Count} items");

// Conditional update: succeeds once, then the condition fails.
var key = new Dictionary<string, AttributeValue> { ["PK"] = new("CUSTOMER#bob"), ["SK"] = new("ORDER#001") };
var update = new UpdateItemRequest
{
    TableName = table,
    Key = key,
    UpdateExpression = "SET #s = :new ADD Revision :one",
    ConditionExpression = "#s = :old",
    ExpressionAttributeNames = new() { ["#s"] = "Status" },
    ExpressionAttributeValues = new() { [":new"] = new("DELIVERED"), [":old"] = new("SHIPPED"), [":one"] = new AttributeValue { N = "1" } },
    ReturnValues = ReturnValue.ALL_NEW,
};
var updated = await ddb.UpdateItemAsync(update);
Console.WriteLine($"Updated bob/ORDER#001 -> {updated.Attributes["Status"].S} (rev {updated.Attributes["Revision"].N})");
try
{
    await ddb.UpdateItemAsync(update);
}
catch (ConditionalCheckFailedException)
{
    Console.WriteLine("Second conditional update rejected with ConditionalCheckFailedException, as expected");
}

// Transactions.
await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
{
    TransactItems =
    [
        new TransactWriteItem { Delete = new Amazon.DynamoDBv2.Model.Delete { TableName = table, Key = new() { ["PK"] = new("CUSTOMER#carol"), ["SK"] = new("ORDER#001") } } },
        new TransactWriteItem { Put = new Amazon.DynamoDBv2.Model.Put { TableName = table, Item = new() { ["PK"] = new("CUSTOMER#carol"), ["SK"] = new("ORDER#005"), ["Status"] = new("PENDING"), ["Total"] = new AttributeValue { N = "99" } } } },
    ],
});
Console.WriteLine("TransactWriteItems: deleted carol/ORDER#001, added carol/ORDER#005");

// Paginated scan.
var pages = 0;
var scanned = 0;
Dictionary<string, AttributeValue>? lastKey = null;
do
{
    var page = await ddb.ScanAsync(new ScanRequest { TableName = table, Limit = 5, ExclusiveStartKey = lastKey });
    pages++;
    scanned += page.Count ?? 0;
    lastKey = page.LastEvaluatedKey is { Count: > 0 } k ? k : null;
} while (lastKey is not null);
Console.WriteLine($"Scan with Limit=5: {scanned} items over {pages} pages");

// High-level object persistence model works too.
var context = new DynamoDBContextBuilder().WithDynamoDBClient(() => ddb).Build();
await context.SaveAsync(new Order { PK = "CUSTOMER#dave", SK = "ORDER#001", Status = "PENDING", Total = 42.0m });
var loaded = await context.LoadAsync<Order>("CUSTOMER#dave", "ORDER#001");
Console.WriteLine($"DynamoDBContext round-trip: {loaded.PK}/{loaded.SK} {loaded.Status} {loaded.Total}");

// Rough throughput.
var sw = Stopwatch.StartNew();
const int n = 2000;
for (var i = 0; i < n; i++)
    await ddb.GetItemAsync(table, key);
Console.WriteLine($"{n} sequential GetItem calls: {sw.ElapsedMilliseconds} ms ({sw.Elapsed.TotalMicroseconds / n:F0} µs/call)");


Console.WriteLine("\n== SQS");
using var sqs = emulator.CreateSqsClient();
var workQueue = (await sqs.CreateQueueAsync("work")).QueueUrl;
await sqs.SendMessageBatchAsync(workQueue, [.. Enumerable.Range(1, 3).Select(i => new SendMessageBatchRequestEntry($"{i}", $"job {i}"))]);
var jobs = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = workQueue, MaxNumberOfMessages = 10 });
Console.WriteLine($"Sent a batch of 3, received: {string.Join(", ", jobs.Messages.Select(m => m.Body).Order())}");

Console.WriteLine("\n== S3");
using var s3 = emulator.CreateS3Client();
await s3.PutBucketAsync("reports");
await s3.PutObjectAsync(new PutObjectRequest { BucketName = "reports", Key = "2026/11/summary.json", ContentBody = """{"orders":12}""", ContentType = "application/json" });
using (var report = await s3.GetObjectAsync("reports", "2026/11/summary.json"))
using (var reader = new StreamReader(report.ResponseStream))
    Console.WriteLine($"PutObject + GetObject: {await reader.ReadToEndAsync()} ({report.Headers.ContentType})");
var keys = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = "reports", Prefix = "2026/" });
Console.WriteLine($"ListObjectsV2 prefix 2026/: {string.Join(", ", keys.S3Objects.Select(o => $"{o.Key} ({o.Size} B)"))}");

Console.WriteLine("\n== Cross-service: an order placed event fans out");
// EventBridge rule -> SNS topic -> two SQS queues (one filtered), all in the same in-memory engine.
using var sns = emulator.CreateSnsClient();
using var events = emulator.CreateEventBridgeClient();

var topicArn = (await sns.CreateTopicAsync("order-placed")).TopicArn;
var ruleArn = (await events.PutRuleAsync(new PutRuleRequest { Name = "order-placed", EventPattern = """{"source":["shop"],"detail-type":["OrderPlaced"]}""" })).RuleArn;
await sns.SetTopicAttributesAsync(topicArn, "Policy", Policy("sns:Publish", topicArn, "events.amazonaws.com", ruleArn));
await events.PutTargetsAsync(new PutTargetsRequest { Rule = "order-placed", Targets = [new Target { Id = "topic", Arn = topicArn, InputPath = "$.detail" }] });

var billing = await Subscribe("billing", filterPolicy: null);
var vip = await Subscribe("vip-desk", filterPolicy: """{"tier":["gold"]}""");

foreach (var (id, tier) in new[] { ("o-1", "silver"), ("o-2", "gold") })
    await events.PutEventsAsync(new PutEventsRequest { Entries = [new() { Source = "shop", DetailType = "OrderPlaced", Detail = JsonSerializer.Serialize(new { id, tier }) }] });

foreach (var (name, url) in new[] { ("billing", billing), ("vip-desk", vip) })
{
    var got = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
    Console.WriteLine($"{name,-8} received: {string.Join(", ", got.Messages.Select(m => m.Body))}");
}

async Task<string> Subscribe(string queueName, string? filterPolicy)
{
    var url = (await sqs.CreateQueueAsync(queueName)).QueueUrl;
    var arn = (await sqs.GetQueueAttributesAsync(url, [QueueAttributeName.QueueArn])).QueueARN;
    await sqs.SetQueueAttributesAsync(url, new() { [QueueAttributeName.Policy] = Policy("sqs:SendMessage", arn, "sns.amazonaws.com", topicArn) });
    var attributes = new Dictionary<string, string> { ["RawMessageDelivery"] = "true" };
    if (filterPolicy is not null)
    {
        attributes["FilterPolicy"] = filterPolicy;
        attributes["FilterPolicyScope"] = "MessageBody";
    }
    await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = arn, Attributes = attributes });
    return url;
}

// A resource policy letting one AWS service principal act on a resource, scoped to a source ARN.
static string Policy(string action, string resource, string principal, string sourceArn) => JsonSerializer.Serialize(new
{
    Version = "2012-10-17",
    Statement = new[]
    {
        new
        {
            Effect = "Allow",
            Principal = new { Service = principal },
            Action = action,
            Resource = resource,
            Condition = new { ArnEquals = new Dictionary<string, string> { ["aws:SourceArn"] = sourceArn } },
        },
    },
});

[DynamoDBTable("Orders")]
public class Order
{
    [DynamoDBHashKey] public string PK { get; set; } = "";
    [DynamoDBRangeKey] public string SK { get; set; } = "";
    public string Status { get; set; } = "";
    public decimal Total { get; set; }
}
