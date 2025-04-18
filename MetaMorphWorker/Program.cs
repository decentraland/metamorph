using Amazon.S3;
using Amazon.SQS;
using MetaMorphAPI.Services;
using MetaMorphAPI.Services.Cache;
using MetaMorphAPI.Services.Queue;
using Serilog;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

// Logging
builder.Logging.ClearProviders();
builder.Logging.AddSerilog(new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger()
);

// Paths
var tempDirectory = Path.Combine(builder.Environment.ContentRootPath, "temp");
Directory.CreateDirectory(tempDirectory);

// Services
builder.Services.AddHttpClient();
builder.Services.AddSingleton<FileAnalyzerService>();
builder.Services.AddHostedService<ConversionBackgroundService>();
builder.Services.AddSingleton<ConverterService>(sp =>
    new ConverterService(tempDirectory, sp.GetRequiredService<FileAnalyzerService>(),sp.GetRequiredService<ILogger<ConverterService>>()));
builder.Services.AddSingleton<DownloadService>(sp =>
    new DownloadService(tempDirectory, sp.GetRequiredService<HttpClient>()));
builder.Services.AddHostedService<ConversionBackgroundService>();


// Params
var awsServiceUrl = GetRequiredConfig<string>("AWS:ServiceURL");
var awsAccessKeyId = GetRequiredConfig<string>("AWS:AccessKeyId");
var awsSecretAccessKey = GetRequiredConfig<string>("AWS:SecretAccessKey");
var sqsQueueUrl = GetRequiredConfig<string>("AWS:SQSQueueURL");
var s3BucketName = GetRequiredConfig<string>("AWS:S3BucketName");
var s3ForcePathStyle = GetRequiredConfig<bool>("AWS:S3ForcePathStyle");
var redisConnectionString = GetRequiredConfig<string>("Redis:ConnectionString");

// Redis
builder.Services.AddSingleton<ConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnectionString));

// SQS
var sqsConfig = new AmazonSQSConfig { ServiceURL = awsServiceUrl };
var sqsClient = new AmazonSQSClient(awsAccessKeyId, awsSecretAccessKey, sqsConfig);
builder.Services.AddSingleton<IAmazonSQS>(sqsClient);
builder.Services.AddSingleton<IConversionQueue>(sp => new RemoteConversionQueue(sqsClient, sqsQueueUrl,
    sp.GetRequiredService<ConnectionMultiplexer>(), sp.GetRequiredService<ILogger<RemoteConversionQueue>>()));

// S3
var s3Config = new AmazonS3Config
{
    ServiceURL = awsServiceUrl,
    ForcePathStyle = s3ForcePathStyle
};
var s3Client = new AmazonS3Client(awsAccessKeyId, awsSecretAccessKey, s3Config);
builder.Services.AddSingleton<IAmazonS3>(s3Client);

// Register the RemoteCacheService with all its dependencies.
builder.Services.AddSingleton<ICacheService>(sp => new RemoteCacheService(
    sp.GetRequiredService<IAmazonS3>(),
    s3BucketName, sp.GetRequiredService<ConnectionMultiplexer>(),
    sp.GetRequiredService<ILogger<RemoteCacheService>>()
));

var host = builder.Build();
host.Run();

return;

T GetRequiredConfig<T>(string key)
{
    var section = builder.Configuration.GetSection(key);
    if (!section.Exists())
        throw new InvalidOperationException($"Missing required configuration: {key}");

    return section.Get<T>()!;
}