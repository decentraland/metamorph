using System.Diagnostics;
using Amazon.S3;
using Amazon.S3.Util;
using Amazon.SQS;
using Amazon.SQS.Model;
using Serilog;

namespace MetaMorphAPI.Utils;

/// <summary>
/// Sets up LocalStack and Redis for development.
/// </summary>
public static class LocalInfra
{
    private const string REDIS_CONTAINER = "metamorph-redis";

    public static async Task EnsureLocalStackRunningAsync()
    {
        using var client = new HttpClient();

        // Check if it's running
        var running = await EnsureLocalStackRunning(0);

        if (running)
        {
            Log.Information("✅ LocalStack is running.");
            return;
        }

        Log.Information("🔧 Starting LocalStack via CLI...");
        await ExecuteCommand("localstack", "start -d");

        // Give LocalStack a moment to boot
        running = await EnsureLocalStackRunning(10);

        if (running)
        {
            Log.Information("✅ LocalStack is running.");
        }
    }

    public static async Task SetupLocalStack(IServiceScope scope, string bucketName, string queueName)
    {
        var s3 = scope.ServiceProvider.GetRequiredService<IAmazonS3>();
        var sqs = scope.ServiceProvider.GetRequiredService<IAmazonSQS>();

        // Ensure S3 bucket exists
        if (!await AmazonS3Util.DoesS3BucketExistV2Async(s3, bucketName))
        {
            await s3.PutBucketAsync(bucketName);
        }

        // Ensure SQS queue exists
        try
        {
            await sqs.GetQueueUrlAsync(queueName);
        }
        catch (QueueDoesNotExistException)
        {
            await sqs.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = queueName
            });
        }
    }
    
    /// <summary>
    /// Ensures the Docker daemon is running.
    /// </summary>
    public static async Task EnsureDockerRunningAsync()
    {
        Log.Information("🔧 Checking Docker daemon...");

        try
        {
            await ExecuteCommand("docker", "info");
        }
        catch
        {
            Log.Error("Could not connect to Docker daemon. Ensure Docker is installed and running.");
            throw;
        }

        Log.Information("✅ Docker daemon is running.");
    }

    public static async Task EnsureRedisRunningAsync()
    {
        // Check if Redis container exists
        var containerExists =
            !string.IsNullOrEmpty(await ExecuteCommand("docker", $"ps -a -q --filter \"name={REDIS_CONTAINER}\""));

        if (containerExists)
        {
            var containerRunning =
                bool.Parse(await ExecuteCommand("docker", $"inspect -f {{{{.State.Running}}}} {REDIS_CONTAINER}"));

            if (!containerRunning)
            {
                Log.Information($"🔁 Starting existing Redis container ({REDIS_CONTAINER})...");
                await ExecuteCommand("docker", $"start {REDIS_CONTAINER}");
            }
        }
        else
        {
            Log.Information("🔧 Creating and starting new Redis container...");
            await ExecuteCommand("docker", $"run -d --name {REDIS_CONTAINER} -p 6379:6379 redis:latest");
        }

        if (await EnsureRedisRunning(10))
        {
            Log.Information("✅ Redis is running.");
        }
        else
        {
            throw new Exception("Could not start Redis.");
        }
    }

    private static async Task<bool> EnsureRedisRunning(int retries)
    {
        for (var i = 0; i < retries; i++)
        {
            var response = await ExecuteCommand("docker", $"exec -it {REDIS_CONTAINER} redis-cli ping");
            var running = response == "PONG";
            if (running)
            {
                return true;
            }

            await Task.Delay(1000);
        }

        return false;
    }

    private static async Task<bool> EnsureLocalStackRunning(int retries)
    {
        using var client = new HttpClient();

        for (var i = 0; i < retries + 1; i++)
        {
            try
            {
                var response = await client.GetAsync("http://localhost:4566/");

                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch
            {
                // ignored
            }

            await Task.Delay(1000);
        }

        return false;
    }

    private static async Task<string> ExecuteCommand(string command, string arguments)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process();
        process.StartInfo = processStartInfo;
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output.Trim();
    }
}