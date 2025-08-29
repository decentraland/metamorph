# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

MetaMorph is a media converter service that converts images and videos into Explorer-friendly formats (KTX2 and MP4). It's built as a .NET 9.0 solution with three main projects:

- **MetaMorphAPI**: Web API service that handles conversion requests
- **MetaMorphWorker**: Background worker for processing conversion jobs  
- **Tests**: NUnit test project with measurement and file type tests

## Architecture

The system supports two deployment modes:

1. **Local Mode**: Self-contained with local file cache and embedded worker
2. **Distributed Mode**: Uses AWS S3/SQS and Redis with separate worker processes

### Key Components

- **Controllers/ConvertController.cs**: Main API endpoint for media conversion
- **Services/ConverterService.cs**: Core conversion logic using ImageMagick and FFmpeg
- **Services/Cache/**: Cache abstraction with local and remote (Redis/S3) implementations
- **Services/Queue/**: Queue abstraction for local and remote (SQS) job processing
- **Utils/BootstrapHelper.cs**: Service registration and configuration setup

## Development Commands

### Building and Running

```bash
# Build the solution
dotnet build MetaMorph.sln

# Run API locally (self-contained mode)
dotnet run --project MetaMorphAPI --launch-profile "Local Contained"

# Run with AWS services (requires LocalStack)
dotnet run --project MetaMorphAPI --launch-profile "Local AWS"

# Run worker separately
dotnet run --project MetaMorphWorker
```

### Testing

```bash
# Run all tests
dotnet test Tests/Tests.csproj

# Run specific test file
dotnet test Tests/Tests.csproj --filter "ClassName=FileTypeTests"
```

### Docker Development

```bash
# Start full environment (API + Worker + Redis + LocalStack)
docker-compose up --build

# API will be available at http://localhost:5133
# Metrics at http://localhost:7053/metrics
```

## Configuration Profiles

- **Local Contained**: Uses local file cache and embedded worker (no external dependencies)
- **Local AWS**: Uses LocalStack S3/SQS with Redis, embedded worker
- **Production**: Uses real AWS services with separate worker processes

## Key Dependencies

- **ImageMagick**: Image processing and conversion
- **FFmpeg**: Video processing and conversion
- **toktx**: KTX2 texture compression (UASTC, ASTC formats)
- **Redis**: Distributed caching and job status tracking
- **AWS SDK**: S3 storage and SQS queuing
- **Prometheus**: Metrics collection
- **Sentry**: Error tracking in production

## API Usage

Single endpoint: `GET /convert?url=<media_url>&imageFormat=<format>&videoFormat=<format>&wait=<bool>`

Supported formats:
- Images: `uastc` (default), `astc`, `astc_high` → KTX2
- Videos: `mp4` (default), `ogv` → MP4/OGV

## File Structure Conventions

- Temp files: `temp/` directory (configurable)
- Converted output: `wwwroot/converted/` (local mode) or S3 bucket
- Static assets for tests: `Tests/Assets/`
- HTTP request examples: `Tests/Requests/`