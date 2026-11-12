using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Xunit;

namespace AwsEmulator.DynamoDb.Tests;

public sealed class DynamoDbEmulatorTests : IDisposable
{
    private readonly DynamoDbEmulator _emulator = new();
    private readonly IAmazonDynamoDB _ddb;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DynamoDbEmulatorTests() => _ddb = _emulator.CreateClient();

    public void Dispose()
    {
        _ddb.Dispose();
        _emulator.Dispose();
    }

    private static async Task CreateTable(IAmazonDynamoDB ddb, string name)
    {
        await ddb.CreateTableAsync(new CreateTableRequest
        {
        TableName = name,
        BillingMode = BillingMode.PAY_PER_REQUEST,
        AttributeDefinitions = [new("PK", ScalarAttributeType.S), new("SK", ScalarAttributeType.S)],
        KeySchema = [new("PK", KeyType.HASH), new("SK", KeyType.RANGE)],
        }, Ct);
        await ddb.WaitForTableActiveAsync(name, Ct);
    }

    [Fact]
    public async Task Put_then_get_round_trips_all_attribute_types()
    {
        await CreateTable(_ddb, "Things");
        var item = new Dictionary<string, AttributeValue>
        {
            ["PK"] = new("a"),
            ["SK"] = new("b"),
            ["Num"] = new AttributeValue { N = "12345678901234567890.123" },
            ["Bin"] = new AttributeValue { B = new MemoryStream([1, 2, 3]) },
            ["Bool"] = new AttributeValue { BOOL = true },
            ["List"] = new AttributeValue { L = [new("x"), new AttributeValue { N = "1" }] },
            ["Map"] = new AttributeValue { M = new() { ["nested"] = new("y") } },
            ["SS"] = new AttributeValue { SS = ["p", "q"] },
        };
        await _ddb.PutItemAsync("Things", item, Ct);

        var got = await _ddb.GetItemAsync("Things", new() { ["PK"] = new("a"), ["SK"] = new("b") }, Ct);

        Assert.Equal("12345678901234567890.123", got.Item["Num"].N);
        Assert.Equal(new byte[] { 1, 2, 3 }, got.Item["Bin"].B.ToArray());
        Assert.True(got.Item["Bool"].BOOL);
        Assert.Equal("y", got.Item["Map"].M["nested"].S);
        Assert.Equal(["p", "q"], got.Item["SS"].SS.Order());
    }

    [Fact]
    public async Task New_table_is_usable_immediately_without_waiting()
    {
        // Upstream aws-emulator holds a new table in CREATING for 3s; native/patches makes it instant.
        var created = await _ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = "Instant",
            BillingMode = BillingMode.PAY_PER_REQUEST,
            AttributeDefinitions = [new("PK", ScalarAttributeType.S)],
            KeySchema = [new("PK", KeyType.HASH)],
        }, Ct);
        await _ddb.PutItemAsync("Instant", new() { ["PK"] = new("a") }, Ct);

        // Trade-off of the patch: with a zero-length transition the CreateTable response already says
        // ACTIVE, where real AWS (and upstream aws-emulator) answers CREATING.
        Assert.Equal(TableStatus.ACTIVE, created.TableDescription.TableStatus);
        Assert.Equal(TableStatus.ACTIVE, (await _ddb.DescribeTableAsync("Instant", Ct)).Table.TableStatus);
    }

    [Fact]
    public async Task Query_honours_key_conditions_and_sort_order()
    {
        await CreateTable(_ddb, "Things");
        foreach (var sk in new[] { "b", "a", "c", "z" })
            await _ddb.PutItemAsync("Things", new() { ["PK"] = new("p"), ["SK"] = new(sk) }, Ct);
        await _ddb.PutItemAsync("Things", new() { ["PK"] = new("other"), ["SK"] = new("a") }, Ct);

        var result = await _ddb.QueryAsync(new QueryRequest
        {
            TableName = "Things",
            KeyConditionExpression = "PK = :pk AND SK BETWEEN :lo AND :hi",
            ExpressionAttributeValues = new() { [":pk"] = new("p"), [":lo"] = new("a"), [":hi"] = new("c") },
            ScanIndexForward = false,
        }, Ct);

        Assert.Equal(["c", "b", "a"], result.Items.Select(i => i["SK"].S));
    }

    [Fact]
    public async Task Service_errors_surface_as_typed_sdk_exceptions()
    {
        await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            _ddb.GetItemAsync("missing", new() { ["PK"] = new("a") }, Ct));

        await CreateTable(_ddb, "Things");
        await Assert.ThrowsAsync<ConditionalCheckFailedException>(() => _ddb.PutItemAsync(new PutItemRequest
        {
            TableName = "Things",
            Item = new() { ["PK"] = new("a"), ["SK"] = new("b") },
            ConditionExpression = "attribute_exists(PK)",
        }, Ct));
    }

    [Fact]
    public async Task Emulators_are_isolated_but_clients_of_one_emulator_share_state()
    {
        await CreateTable(_ddb, "Things");

        using var secondClient = _emulator.CreateClient();
        Assert.Contains("Things", (await secondClient.ListTablesAsync(Ct)).TableNames);

        using var other = new DynamoDbEmulator();
        using var otherClient = other.CreateClient();
        Assert.Empty((await otherClient.ListTablesAsync(Ct)).TableNames ?? []);
    }

    [Fact]
    public async Task Handles_concurrent_requests()
    {
        await CreateTable(_ddb, "Things");
        await Task.WhenAll(Enumerable.Range(0, 200).Select(i =>
            _ddb.PutItemAsync("Things", new() { ["PK"] = new("p"), ["SK"] = new($"{i:D4}") }, Ct)));

        var count = await _ddb.QueryAsync(new QueryRequest
        {
            TableName = "Things",
            KeyConditionExpression = "PK = :pk",
            ExpressionAttributeValues = new() { [":pk"] = new("p") },
            Select = Select.COUNT,
        }, Ct);
        Assert.Equal(200, count.Count);
    }
}
