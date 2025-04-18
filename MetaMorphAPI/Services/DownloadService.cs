namespace MetaMorphAPI.Services;

/// <summary>
/// Handles downloading the original files for conversion.
/// </summary>
public class DownloadService(string tempDirectory, HttpClient httpClient)
{
    
    /// <summary>
    /// Downloads a file from URL and saves it to <see cref="tempDirectory"/> with <see cref="hash"/> filename.
    /// </summary>
    public async Task<string> DownloadFile(string url, string hash)
    {
        using var response = await httpClient.GetAsync(url);

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Unable to download file from: {url} - error({response.StatusCode}): {response.ReasonPhrase}");

        var tempFilePath = Path.Combine(tempDirectory, hash);

        await using var fs = new FileStream(tempFilePath, FileMode.Create);
        await response.Content.CopyToAsync(fs);

        return tempFilePath;
    }
}