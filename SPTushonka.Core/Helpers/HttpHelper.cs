using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ComponentAce.Compression.Libs.zlib;
using Microsoft.Extensions.Logging;
using SPTarkov.Core.SPT;
using SPTarkov.Core.SPT.Responses;

namespace SPTarkov.Core.Helpers;

public class HttpHelper
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpHelper> _logger;
    private readonly StateHelper _stateHelper;

    public HttpHelper(ILogger<HttpHelper> logger, StateHelper stateHelper)
    {
        _logger = logger;
        _stateHelper = stateHelper;

        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = CertificateValidationCallback;
        handler.UseCookies = false;

        _httpClient = new HttpClient(handler);
        _httpClient.DefaultRequestVersion = new Version(3, 0);
        _httpClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
    }

    private static bool CertificateValidationCallback(
        HttpRequestMessage httpRequestMessage,
        X509Certificate2? x509Certificate2,
        X509Chain? x509Chain,
        SslPolicyErrors sslPolicyErrors
    )
    {
        return true;
    }

    private string BuildGameUrl(string url)
    {
        return "https://" + _stateHelper.SelectedServer?.IpAddress + url;
    }

    public async Task<T?> GameServerGet<T>(string url, CancellationToken token)
    {
        _logger.LogDebug("Get: {Url}", url);

        var task = await _httpClient.GetAsync(BuildGameUrl(url), token);
        return JsonSerializer.Deserialize<T>(ReadJson(await task.Content.ReadAsByteArrayAsync(token)));
    }

    /// <summary>
    /// Pings a specific server address, independent of the currently-selected server. Any failure (unreachable, bad response,
    /// cancellation) reads as offline.
    /// </summary>
    public async Task<bool> PingServerAsync(string ipAddress, CancellationToken token)
    {
        try
        {
            var response = await _httpClient.GetAsync("https://" + ipAddress + Urls.Ping, token);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var ping = JsonSerializer.Deserialize<SPTPingResponse>(ReadJson(await response.Content.ReadAsByteArrayAsync(token)));
            return ping?.Response == "Pong!";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PingServerAsync failed for {IpAddress}", ipAddress);
            return false;
        }
    }

    /// <summary>
    /// Streams a file straight to disk. Unlike <see cref="GameServerGet{T}"/> the body is not zlib-wrapped,
    /// so it must not be run through SimpleZlib.
    /// </summary>
    /// <param name="url">Server-relative path to fetch</param>
    /// <param name="destinationPath">Final path to write to</param>
    /// <param name="onProgress">Called with (bytesWritten, totalBytes); totalBytes is -1 when unknown</param>
    /// <param name="token">Cancels the download and removes the partial file</param>
    public async Task DownloadFileAsync(string url, string destinationPath, Action<long, long>? onProgress, CancellationToken token)
    {
        _logger.LogDebug("Download: {Url}", url);

        var directory = Path.GetDirectoryName(destinationPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write to a sibling temp file and move on success, so an interrupted download can never be
        // mistaken for a complete bundle
        var tempPath = destinationPath + ".part";

        try
        {
            using var response = await _httpClient.GetAsync(BuildGameUrl(url), HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;

            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (
                var destination = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true)
            )
            {
                var buffer = new byte[81920];
                long written = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), token);
                    written += read;
                    onProgress?.Invoke(written, totalBytes);
                }
            }

            File.Move(tempPath, destinationPath, true);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
    }

    public async Task<T?> GameServerPut<T>(string url, object request, CancellationToken token)
    {
        _logger.LogDebug("Put: {Url}", url);

        var content = new ByteArrayContent(SimpleZlib.CompressToBytes(JsonSerializer.Serialize(request), zlibConst.Z_BEST_COMPRESSION));

        var task = await _httpClient.PutAsync(BuildGameUrl(url), content, token);

        return JsonSerializer.Deserialize<T>(ReadJson(await task.Content.ReadAsByteArrayAsync(token)));
    }

    /// <summary>
    /// Servers up to 4.1 zlib the launcher responses, 5.0 sends them plain. The zlib header decides.
    /// </summary>
    private static string ReadJson(byte[] body)
    {
        var zlib = body.Length >= 2 && (body[0] & 0x0F) == 8 && ((body[0] << 8) | body[1]) % 31 == 0;
        return zlib ? SimpleZlib.Decompress(body) : Encoding.UTF8.GetString(body);
    }
}
