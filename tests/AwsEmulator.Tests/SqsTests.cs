using Amazon.SQS;
using Amazon.SQS.Model;
using Xunit;

namespace AwsEmulator.Tests;

public sealed class SqsTests : IDisposable
{
    private readonly EmbeddedAwsEmulator _emulator = new();
    private readonly IAmazonSQS _sqs;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SqsTests() => _sqs = _emulator.CreateSqsClient();

    public void Dispose()
    {
        _sqs.Dispose();
        _emulator.Dispose();
    }

    [Fact]
    public async Task Send_receive_delete_round_trip_with_attributes()
    {
        var (url, _) = await Queues.CreateQueueAsync(_sqs, "orders", Ct);

        await _sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = "hello",
            MessageAttributes = new() { ["kind"] = new MessageAttributeValue { DataType = "String", StringValue = "greeting" } },
        }, Ct);

        var messages = await Queues.ReceiveAsync(_sqs, url, 1, Ct);
        var message = Assert.Single(messages);
        Assert.Equal("hello", message.Body);
        Assert.Equal("greeting", message.MessageAttributes["kind"].StringValue);

        var empty = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, WaitTimeSeconds = 0 }, Ct);
        Assert.Empty(empty.Messages ?? []);
    }

    [Fact]
    public async Task Batch_send_delivers_every_entry()
    {
        var (url, _) = await Queues.CreateQueueAsync(_sqs, "batch", Ct);
        var result = await _sqs.SendMessageBatchAsync(url,
            [.. Enumerable.Range(0, 10).Select(i => new SendMessageBatchRequestEntry($"m{i}", $"body {i}"))], Ct);
        Assert.Equal(10, result.Successful.Count);

        var messages = await Queues.ReceiveAsync(_sqs, url, 10, Ct);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"body {i}").Order(), messages.Select(m => m.Body).Order());
    }

    [Fact]
    public async Task Fifo_queue_preserves_order_and_deduplicates()
    {
        var url = (await _sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "jobs.fifo",
            Attributes = new() { [QueueAttributeName.FifoQueue] = "true" },
        }, Ct)).QueueUrl;

        foreach (var (body, dedup) in new[] { ("first", "1"), ("second", "2"), ("second-again", "2"), ("third", "3") })
            await _sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body, MessageGroupId = "g", MessageDeduplicationId = dedup }, Ct);

        var bodies = (await Queues.ReceiveAsync(_sqs, url, 3, Ct)).Select(m => m.Body);
        Assert.Equal(["first", "second", "third"], bodies);
    }

    [Fact]
    public async Task Missing_queue_surfaces_typed_exception()
    {
        await Assert.ThrowsAsync<QueueDoesNotExistException>(() => _sqs.GetQueueUrlAsync("nope", Ct));
    }
}
