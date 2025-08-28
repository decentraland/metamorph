using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using MetaMorphAPI.Services.Cache;
using StackExchange.Redis;

namespace MetaMorphAPI.Services.Queue;

/// <summary>
/// A remote version of the conversion queue using Amazon SQS.
/// </summary>
public class RemoteConversionQueue(
    IAmazonSQS sqsClient,
    string queueName,
    ConnectionMultiplexer redis,
    ILogger<RemoteConversionQueue> logger) : IConversionQueue
{
    private readonly TimeSpan _conversionExpiry = TimeSpan.FromMinutes(10);
    private readonly IDatabase _redisDb = redis.GetDatabase();

    private readonly Lazy<Task<string>> _queueUrlLazy =
        new(async () => (await sqsClient.GetQueueUrlAsync(queueName)).QueueUrl);

    public async Task Enqueue(ConversionJob job, CancellationToken ct = default)
    {
        if (!await _redisDb.StringSetAsync(RedisKeys.GetConvertingKey(job.Hash, job.ImageFormat, job.VideoFormat), "1", _conversionExpiry, When.NotExists))
        {
            // Already queued
            logger.LogInformation("Conversion already enqueued, skipping: {Hash}", job.Hash);
            return;
        }

        var messageBody = JsonSerializer.Serialize(job);
        var request = new SendMessageRequest
        {
            QueueUrl = await _queueUrlLazy.Value,
            MessageBody = messageBody
        };
        await sqsClient.SendMessageAsync(request, ct);
    }

    public async Task<ConversionJob> Dequeue(CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var request = new ReceiveMessageRequest
            {
                QueueUrl = await _queueUrlLazy.Value,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 20 // Long polling for up to 20 seconds
            };

            var response = await sqsClient.ReceiveMessageAsync(request, ct);

            if (response.Messages.Count > 0)
            {
                var message = response.Messages[0];
                var job = JsonSerializer.Deserialize<ConversionJob>(message.Body);


                // Delete the message from the queue after processing
                await sqsClient.DeleteMessageAsync(new DeleteMessageRequest
                {
                    QueueUrl = await _queueUrlLazy.Value,
                    ReceiptHandle = message.ReceiptHandle
                }, ct);

                if (job == null)
                {
                    throw new InvalidOperationException("Failed to deserialize queue job");
                }

                return job;
            }
        }
    }
}