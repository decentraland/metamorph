using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using StackExchange.Redis;

namespace MetaMorphAPI.Services.Queue;

/// <summary>
/// A remote version of the conversion queue using Amazon SQS.
/// </summary>
public class RemoteConversionQueue(
    IAmazonSQS sqsClient,
    string queueUrl,
    ConnectionMultiplexer redis,
    ILogger<RemoteConversionQueue> logger) : IConversionQueue
{
    private readonly TimeSpan _conversionExpiry = TimeSpan.FromMinutes(10);
    private readonly IDatabase _redisDb = redis.GetDatabase();

    public async Task Enqueue(ConversionJob job, CancellationToken ct = default)
    {
        var redisKey = $"converting:{job.Hash}";
        if (!await _redisDb.StringSetAsync(redisKey, "1", _conversionExpiry, When.NotExists))
        {
            // Already queued
            logger.LogInformation("Conversion already enqueued, skipping: {Hash}", job.Hash);
            return;
        }

        var messageBody = JsonSerializer.Serialize(job);
        var request = new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = messageBody,
            // MessageGroupId = "conversion-group",
            // MessageDeduplicationId = job.Hash
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
                QueueUrl = queueUrl,
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
                    QueueUrl = queueUrl,
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