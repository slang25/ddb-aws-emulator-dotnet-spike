using System.Text.Json;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Xunit;

namespace AwsEmulator.Tests;

public sealed class SnsTests : IDisposable
{
    private readonly EmbeddedAwsEmulator _emulator = new();
    private readonly IAmazonSimpleNotificationService _sns;
    private readonly IAmazonSQS _sqs;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SnsTests()
    {
        _sns = _emulator.CreateSnsClient();
        _sqs = _emulator.CreateSqsClient();
    }

    public void Dispose()
    {
        _sns.Dispose();
        _sqs.Dispose();
        _emulator.Dispose();
    }

    [Fact]
    public async Task Topic_lifecycle()
    {
        var arn = (await _sns.CreateTopicAsync("events", Ct)).TopicArn;
        Assert.Contains(arn, (await _sns.ListTopicsAsync(Ct)).Topics.Select(t => t.TopicArn));

        var published = await _sns.PublishAsync(arn, "no subscribers yet", Ct);
        Assert.False(string.IsNullOrEmpty(published.MessageId));

        await _sns.DeleteTopicAsync(arn, Ct);
        Assert.DoesNotContain(arn, (await _sns.ListTopicsAsync(Ct)).Topics?.Select(t => t.TopicArn) ?? []);
    }

    [Fact]
    public async Task Publish_fans_out_to_subscribed_sqs_queue()
    {
        var topicArn = (await _sns.CreateTopicAsync("orders", Ct)).TopicArn;
        var (queueUrl, queueArn) = await Queues.CreateQueueAsync(_sqs, "orders-sub", Ct);
        await Queues.AllowSendFromAsync(_sqs, queueUrl, queueArn, "sns.amazonaws.com", topicArn, Ct);
        await _sns.SubscribeAsync(topicArn, "sqs", queueArn, Ct);

        await _sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = "order placed", Subject = "hi" }, Ct);

        var message = Assert.Single(await Queues.ReceiveAsync(_sqs, queueUrl, 1, Ct));
        using var envelope = JsonDocument.Parse(message.Body);
        Assert.Equal("Notification", envelope.RootElement.GetProperty("Type").GetString());
        Assert.Equal("order placed", envelope.RootElement.GetProperty("Message").GetString());
        Assert.Equal(topicArn, envelope.RootElement.GetProperty("TopicArn").GetString());
    }

    [Fact]
    public async Task Raw_delivery_and_filter_policy_are_honoured()
    {
        var topicArn = (await _sns.CreateTopicAsync("filtered", Ct)).TopicArn;
        var (queueUrl, queueArn) = await Queues.CreateQueueAsync(_sqs, "filtered-sub", Ct);
        await Queues.AllowSendFromAsync(_sqs, queueUrl, queueArn, "sns.amazonaws.com", topicArn, Ct);
        await _sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new()
            {
                ["RawMessageDelivery"] = "true",
                ["FilterPolicy"] = """{"colour":["red"]}""",
            },
        }, Ct);

        foreach (var colour in new[] { "blue", "red" })
        {
            await _sns.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = $"{colour} thing",
                MessageAttributes = new() { ["colour"] = new MessageAttributeValue { DataType = "String", StringValue = colour } },
            }, Ct);
        }

        var message = Assert.Single(await Queues.ReceiveAsync(_sqs, queueUrl, 2, Ct, maxPolls: 2));
        Assert.Equal("red thing", message.Body);
    }
}
