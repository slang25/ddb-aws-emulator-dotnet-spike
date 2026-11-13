using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace AwsEmulator.Tests;

/// <summary>SQS helpers shared by the cross-service tests.</summary>
internal static class Queues
{
    public static async Task<(string Url, string Arn)> CreateQueueAsync(IAmazonSQS sqs, string name, CancellationToken ct)
    {
        var url = (await sqs.CreateQueueAsync(name, ct)).QueueUrl;
        var attributes = await sqs.GetQueueAttributesAsync(url, [QueueAttributeName.QueueArn], ct);
        return (url, attributes.QueueARN);
    }

    /// <summary>Lets <paramref name="sourceArn"/> (an SNS topic, EventBridge rule, or S3 bucket) send to the queue, as real AWS requires.</summary>
    public static Task AllowSendFromAsync(IAmazonSQS sqs, string queueUrl, string queueArn, string servicePrincipal, string sourceArn, CancellationToken ct)
    {
        var policy = JsonSerializer.Serialize(new
        {
            Version = "2012-10-17",
            Statement = new[]
            {
                new
                {
                    Effect = "Allow",
                    Principal = new { Service = servicePrincipal },
                    Action = "sqs:SendMessage",
                    Resource = queueArn,
                    Condition = new { ArnEquals = new Dictionary<string, string> { ["aws:SourceArn"] = sourceArn } },
                },
            },
        });
        return sqs.SetQueueAttributesAsync(queueUrl, new Dictionary<string, string> { [QueueAttributeName.Policy] = policy }, ct);
    }

    /// <summary>Long-polls until <paramref name="count"/> messages arrive (or the wait gives up), deleting them.</summary>
    public static async Task<List<Message>> ReceiveAsync(IAmazonSQS sqs, string queueUrl, int count, CancellationToken ct, int maxPolls = 3)
    {
        var received = new List<Message>();
        for (var poll = 0; poll < maxPolls && received.Count < count; poll++)
        {
            var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 2,
                MessageAttributeNames = ["All"],
                MessageSystemAttributeNames = ["All"],
            }, ct);
            foreach (var message in response.Messages ?? [])
            {
                received.Add(message);
                await sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, ct);
            }
        }
        return received;
    }
}
