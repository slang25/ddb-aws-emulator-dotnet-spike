using System.Text.Json;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS;
using Xunit;

namespace AwsEmulator.Tests;

public sealed class EventBridgeTests : IDisposable
{
    private readonly EmbeddedAwsEmulator _emulator = new();
    private readonly IAmazonEventBridge _events;
    private readonly IAmazonSQS _sqs;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public EventBridgeTests()
    {
        _events = _emulator.CreateEventBridgeClient();
        _sqs = _emulator.CreateSqsClient();
    }

    public void Dispose()
    {
        _events.Dispose();
        _sqs.Dispose();
        _emulator.Dispose();
    }

    [Fact]
    public async Task Custom_bus_and_rules_can_be_managed()
    {
        await _events.CreateEventBusAsync(new CreateEventBusRequest { Name = "app" }, Ct);
        await _events.PutRuleAsync(new PutRuleRequest { Name = "r1", EventBusName = "app", EventPattern = """{"source":["app.orders"]}""" }, Ct);

        var rules = await _events.ListRulesAsync(new ListRulesRequest { EventBusName = "app" }, Ct);
        Assert.Equal("r1", Assert.Single(rules.Rules).Name);
    }

    [Fact]
    public async Task Matching_events_are_routed_to_an_sqs_target()
    {
        var ruleArn = (await _events.PutRuleAsync(new PutRuleRequest
        {
            Name = "orders-placed",
            EventPattern = """{"source":["app.orders"],"detail-type":["OrderPlaced"],"detail":{"total":[{"numeric":[">",100]}]}}""",
        }, Ct)).RuleArn;
        var (queueUrl, queueArn) = await Queues.CreateQueueAsync(_sqs, "big-orders", Ct);
        await Queues.AllowSendFromAsync(_sqs, queueUrl, queueArn, "events.amazonaws.com", ruleArn, Ct);
        await _events.PutTargetsAsync(new PutTargetsRequest { Rule = "orders-placed", Targets = [new Target { Id = "q", Arn = queueArn }] }, Ct);

        var put = await _events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new() { Source = "app.orders", DetailType = "OrderPlaced", Detail = """{"id":"small","total":5}""" },
                new() { Source = "app.orders", DetailType = "OrderPlaced", Detail = """{"id":"big","total":500}""" },
                new() { Source = "app.other", DetailType = "OrderPlaced", Detail = """{"id":"wrong-source","total":500}""" },
            ],
        }, Ct);
        Assert.Equal(0, put.FailedEntryCount ?? 0);

        var message = Assert.Single(await Queues.ReceiveAsync(_sqs, queueUrl, 3, Ct, maxPolls: 2));
        using var evt = JsonDocument.Parse(message.Body);
        Assert.Equal("app.orders", evt.RootElement.GetProperty("source").GetString());
        Assert.Equal("big", evt.RootElement.GetProperty("detail").GetProperty("id").GetString());
    }
}
