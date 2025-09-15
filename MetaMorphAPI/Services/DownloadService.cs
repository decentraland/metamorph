using System.Buffers;

namespace MetaMorphAPI.Services;

/// <summary>
/// Handles downloading the original files for conversion.
/// </summary>
public class DownloadService(string tempDirectory, HttpClient httpClient, long maxFileSizeBytes)
{
    private const int BUFFER_SIZE = 81920;

    /// <summary>
    /// Downloads a file from URL and saves it to <see cref="tempDirectory"/> with <see cref="hash"/> filename.
    /// </summary>
    public async Task<(string path, string? eTag, TimeSpan? maxAge)> DownloadFile(string url, string hash)
    {
        using var response =
            await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new Exception(
                $"Unable to download file from: {url} - error({response.StatusCode}): {response.ReasonPhrase}");

        if (response.Content.Headers.ContentLength.HasValue &&
            response.Content.Headers.ContentLength.Value > maxFileSizeBytes)
            throw new Exception(
                $"File from {url} is too large ({response.Content.Headers.ContentLength.Value} bytes). Maximum allowed size is {maxFileSizeBytes} bytes.");

        // Most of this complexity is so that we can cancel the download mid-way if it exceeds the max file size
        var tempFilePath = Path.Combine(tempDirectory, hash);
        var buffer = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);
        try
        {
            await using var fs = new FileStream(
                tempFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BUFFER_SIZE,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            );
            await using var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            long totalBytesRead = 0;
            int bytesRead;
            while ((bytesRead = await responseStream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            {
                totalBytesRead += bytesRead;
                if (totalBytesRead > maxFileSizeBytes)
                    throw new Exception(
                        $"Downloaded data exceeded the maximum allowed size of {maxFileSizeBytes} bytes.");
                await fs.WriteAsync(buffer.AsMemory(0, bytesRead));
            }
        }
        catch
        {
            if (File.Exists(tempFilePath))
                File.Delete(tempFilePath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var maxAge = response.Headers.CacheControl?.MaxAge;
        if (response.Headers.CacheControl?.NoCache == true)
        {
            // If NoCache is specified we set maxAge to 0 and let the system down the line handle it (it will be set to the minimum maxAge specified).
            maxAge = TimeSpan.FromMinutes(0);
        }

        return (tempFilePath, response.Headers.ETag?.Tag, maxAge);
    }
}