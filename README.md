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