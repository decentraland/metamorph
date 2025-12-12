# MetaMorph

A media converter service for converting images and videos into Explorer friendly formats (KTX2 and MP4).

## Running
You can run the service locally without any additional setup by selecting the "MetaMorphAPI: Local Contained" configuration.

If you want to run it with AWS you need to have LocalStack and Docker (+ docker-compose) installed, then you have two options:

### Docker Compose
To run in docker you can run the following command:

`docker-compose up --build`

This will start all the necessary services (Localstack and Redis), and separate API and Worker services.

### Local AWS
To run from your IDE you can use the "MetaMorphAPI: Local AWS" configuration. This will start LocalStack and a new Redis container, and allow you to run from your IDE. You do not need to run MetaMorphWorker separately as the API service will be running it's own worker background service.

## API Usage

The service exposes a single endpoint for media conversion:

```
GET /convert?url=<media_url>&imageFormat=<format>&videoFormat=<format>&wait=<bool>
```

### Parameters

- **url** (required): The URL of the media file to convert
- **imageFormat** (optional): The desired output format for images. Valid values:
  - `astc` (8x8 block size)
  - `astc_high` (4x4 block size)
  - `uastc` (default)
- **videoFormat** (optional): The desired output format for videos. Valid values:
  - `mp4` (default)
  - `ogv`
- **wait** (optional): If `true`, holds the request until conversion completes (max 20 seconds)
  - On success: redirects to the converted file
  - On timeout: responds with `202 Accepted`
- **forceRefresh** (optional): If `true` a new conversion will be scheduled, even if a valid cached version already exists.
  - Note that the cached version will still be returned if it exists.

Specifying both `videoFormat` and `imageFormat` is valid and the converter will use the appropriate one when it determines if the url contains a video or an image file.

### Examples

```bash
# Convert image to KTX2 with ASTC compression
curl -L "http://localhost:5133/convert?url=https://example.com/image.jpg&imageFormat=astc"

# Convert video to OGV format
curl -L "http://localhost:5133/convert?url=https://example.com/video.mp4&videoFormat=ogv"

# Convert video or image to specified format
curl -L "http://localhost:5133/convert?url=https://example.com/fileOfUnknownFormat&videoFormat=ogv&imageFormat=astc"

# Convert and wait for completion
curl -L "http://localhost:5133/convert?url=https://example.com/image.png&imageFormat=astc_high&wait=true"
```

## Supported Formats

### Input
- **Images**: JPEG, PNG, GIF, WebP, and other formats supported by ImageMagick
- **Videos**: MP4, MOV, AVI, and other formats supported by FFmpeg

### Output
- **Images**: KTX2 container with:
  - UASTC compression (default) (`imageFormat=uastc`)
  - ASTC 8x8 compression (`imageFormat=astc`)
  - ASTC 4x4 compression (`imageFormat=astc_high`)
- **Videos**: 
  - MP4 with H.264 encoding (default) (`videoFormat=mp4`)
  - OGV with Theora encoding (`videoFormat=ogv`)

## Known Issues
- If two requests are made one after another for the same image, but have different `videoFormat` values (or the other way around) the converter will process the image twice.