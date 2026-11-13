using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Amazon.SQS;
using Xunit;

namespace AwsEmulator.Tests;

public sealed class S3Tests : IDisposable
{
    private readonly EmbeddedAwsEmulator _emulator = new();
    private readonly IAmazonS3 _s3;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public S3Tests() => _s3 = _emulator.CreateS3Client();

    public void Dispose()
    {
        _s3.Dispose();
        _emulator.Dispose();
    }

    [Fact]
    public async Task Put_get_head_list_copy_delete()
    {
        await _s3.PutBucketAsync("my-bucket", Ct);
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = "my-bucket",
            Key = "docs/a.txt",
            ContentBody = "hello s3",
            ContentType = "text/plain",
            Metadata = { ["owner"] = "stu" },
        }, Ct);
        await _s3.PutObjectAsync(new PutObjectRequest { BucketName = "my-bucket", Key = "other/b.txt", ContentBody = "b" }, Ct);

        using (var get = await _s3.GetObjectAsync("my-bucket", "docs/a.txt", Ct))
        using (var reader = new StreamReader(get.ResponseStream))
        {
            Assert.Equal("hello s3", await reader.ReadToEndAsync(Ct));
            Assert.Equal("text/plain", get.Headers.ContentType);
        }

        var head = await _s3.GetObjectMetadataAsync("my-bucket", "docs/a.txt", Ct);
        Assert.Equal(8, head.ContentLength);
        Assert.Equal("stu", head.Metadata["owner"]);

        var listed = await _s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = "my-bucket", Prefix = "docs/" }, Ct);
        Assert.Equal("docs/a.txt", Assert.Single(listed.S3Objects).Key);

        await _s3.CopyObjectAsync("my-bucket", "docs/a.txt", "my-bucket", "docs/copy.txt", Ct);
        await _s3.DeleteObjectAsync("my-bucket", "docs/a.txt", Ct);

        var after = await _s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = "my-bucket", Prefix = "docs/" }, Ct);
        Assert.Equal("docs/copy.txt", Assert.Single(after.S3Objects).Key);
    }

    [Fact]
    public async Task Missing_key_surfaces_NoSuchKey()
    {
        await _s3.PutBucketAsync("my-bucket", Ct);
        var ex = await Assert.ThrowsAsync<NoSuchKeyException>(() => _s3.GetObjectAsync("my-bucket", "nope", Ct));
        Assert.Equal("NoSuchKey", ex.ErrorCode);
    }

    [Fact]
    public async Task Binary_and_multipart_uploads_round_trip()
    {
        await _s3.PutBucketAsync("big", Ct);
        var data = new byte[12 * 1024 * 1024];
        Random.Shared.NextBytes(data);

        using var transfer = new TransferUtility(_s3);
        await transfer.UploadAsync(new TransferUtilityUploadRequest
        {
            BucketName = "big",
            Key = "blob.bin",
            InputStream = new MemoryStream(data),
            PartSize = 5 * 1024 * 1024,
        }, Ct);

        using var get = await _s3.GetObjectAsync("big", "blob.bin", Ct);
        using var copy = new MemoryStream();
        await get.ResponseStream.CopyToAsync(copy, Ct);
        Assert.Equal(data, copy.ToArray());
    }

    [Fact]
    public async Task Object_created_notifications_reach_sqs()
    {
        using var sqs = _emulator.CreateSqsClient();
        var (queueUrl, queueArn) = await Queues.CreateQueueAsync(sqs, "uploads", Ct);
        await _s3.PutBucketAsync("inbox", Ct);
        await Queues.AllowSendFromAsync(sqs, queueUrl, queueArn, "s3.amazonaws.com", "arn:aws:s3:::inbox", Ct);
        await _s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = "inbox",
            QueueConfigurations = [new QueueConfiguration { Queue = queueArn, Events = [EventType.ObjectCreatedPut] }],
        }, Ct);
        // S3 sends an s3:TestEvent when a notification configuration is saved; skip past it.
        await _s3.PutObjectAsync(new PutObjectRequest { BucketName = "inbox", Key = "file.txt", ContentBody = "x" }, Ct);

        var records = (await Queues.ReceiveAsync(sqs, queueUrl, 2, Ct, maxPolls: 2))
            .Select(m => JsonDocument.Parse(m.Body).RootElement)
            .Where(e => e.TryGetProperty("Records", out _))
            .ToList();
        var record = Assert.Single(records).GetProperty("Records")[0];
        Assert.StartsWith("ObjectCreated:", record.GetProperty("eventName").GetString());
        Assert.Equal("file.txt", record.GetProperty("s3").GetProperty("object").GetProperty("key").GetString());
    }
}
