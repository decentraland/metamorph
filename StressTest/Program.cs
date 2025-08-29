//const string API = "https://metamorph-api.decentraland.org/convert?url={0}";
 const string API = "https://metamorph-api.decentraland.zone/convert?url={0}";
// const string API = "http://localhost:5133/convert?url={0}";
const string IMG_URL_PNG =
    "https://media.githubusercontent.com/media/decentraland/metamorph/refs/heads/main/Tests/Assets/measurement/img{0}/{1}.png?timestamp={2}_{3}";
const string IMG_URL_JPG = "https://media.githubusercontent.com/media/decentraland/metamorph/refs/heads/main/Tests/Assets/measurement/img{0}/{1}.jpg?timestamp={2}_{3}";

const string IMG_URL_WEBP_ANIM = "https://media.githubusercontent.com/media/decentraland/metamorph/refs/heads/main/Tests/Assets/types/test_animated.webp?timestamp={2}_{3}";
const string IMG_URL_GIF = "https://media.githubusercontent.com/media/decentraland/metamorph/refs/heads/main/Tests/Assets/types/test.gif?timestamp={2}_{3}";
const string IMG_URL_SVG = "https://raw.githubusercontent.com/decentraland/metamorph/refs/heads/main/Tests/Assets/types/test.svg?timestamp={2}_{3}";
const string IMG_URL_MP4 = "https://media.githubusercontent.com/media/decentraland/metamorph/refs/heads/main/Tests/Assets/types/test.mp4?timestamp={2}_{3}";

string[] resolutions = ["5k", "2.5k", "1.5k", "1k", "0.5k"];

const int ITERATIONS = 1;

var timestamp = DateTime.Now.Ticks;
using var httpClient = new HttpClient();
var tasks = new List<Task<HttpResponseMessage>>();

Console.WriteLine($"Starting stress test with timestamp {timestamp}");

for (int i = 0; i < ITERATIONS; i++)
{
    for (int j = 0; j <= 4; j++)
    {
        foreach (var resolution in resolutions)
        {
            // PNG
            tasks.Add(httpClient.GetAsync(
                string.Format(API, Uri.EscapeDataString(string.Format(IMG_URL_PNG, j, resolution, timestamp, i))),
                HttpCompletionOption.ResponseHeadersRead));

            // JPG
            tasks.Add(httpClient.GetAsync(
                string.Format(API, Uri.EscapeDataString(string.Format(IMG_URL_JPG, j, resolution, timestamp, i))),
                HttpCompletionOption.ResponseHeadersRead));
        }
    }
}

for (int i = 0; i < 10; i++)
{
    tasks.Add(httpClient.GetAsync(
        string.Format(API, Uri.EscapeDataString(string.Format(IMG_URL_WEBP_ANIM, 2, "5k", timestamp, i))),
        HttpCompletionOption.ResponseHeadersRead));
}

for (int i = 0; i < 10; i++)
{
    tasks.Add(httpClient.GetAsync(
        string.Format(API, Uri.EscapeDataString(string.Format(IMG_URL_GIF, 2, "5k", timestamp, i))),
        HttpCompletionOption.ResponseHeadersRead));
}

for (int i = 0; i < 10; i++)
{
    tasks.Add(httpClient.GetAsync(
        string.Format(API, Uri.EscapeDataString(string.Format(IMG_URL_SVG, 2, "5k", timestamp, i))),
        HttpCompletionOption.ResponseHeadersRead));
}

for (int i = 0; i < 10; i++)
{
    tasks.Add(httpClient.GetAsync(
        string.Format(API, Uri.EscapeDataString(string.Format(IMG_URL_MP4, 2, "5k", timestamp, i))),
        HttpCompletionOption.ResponseHeadersRead));
}


await Task.WhenAll(tasks);

Console.WriteLine(
    $"Done! Sent requests: {tasks.Count}, Received OK responses: {tasks.Count(t => t.Result.IsSuccessStatusCode)}");