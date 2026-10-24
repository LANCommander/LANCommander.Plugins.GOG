using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace LANCommander.Plugins.GOG.Api;

/// <summary>Raised when a downloaded file does not match what GOG says it should be.</summary>
public sealed class GogDownloadVerificationException(string message) : Exception(message);

/// <summary>
/// Fetches installer files from GOG's CDN.
/// </summary>
/// <remarks>
/// Installers run to tens of gigabytes, so a download that drops resumes from where it got to
/// rather than starting over. CDN addresses are signed and expire, so each attempt asks for a fresh
/// one.
/// </remarks>
public sealed class GogDownloader(HttpClient http, ILogger<GogDownloader> logger)
{
    private const int MaxAttempts = 5;

    /// <summary>How long to wait before the given retry. Tests shorten it.</summary>
    internal Func<int, TimeSpan> RetryDelay { get; set; } = attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt));

    /// <summary>
    /// Downloads one file into <paramref name="directory"/>, named as the CDN names it.
    /// </summary>
    /// <param name="resolve">Produces a current download address. Called once per attempt.</param>
    /// <param name="directory">Where to put the file.</param>
    /// <param name="expectedSize">The size GOG reports, or 0 when unknown.</param>
    /// <param name="verify">Check the file against the MD5 GOG publishes for it.</param>
    /// <param name="progress">Receives the number of bytes of this file on disk so far.</param>
    /// <param name="cancellationToken">Cancels the download. A partial file is kept for resuming.</param>
    /// <returns>The path of the finished file.</returns>
    public async Task<string> DownloadAsync(
        Func<CancellationToken, Task<GogDownlink>> resolve,
        string directory,
        long expectedSize,
        bool verify,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);

        for (var attempt = 1; ; attempt++)
        {
            var link = await resolve(cancellationToken);
            var path = Path.Combine(directory, FileName(link.Url!));

            try
            {
                await DownloadToAsync(link.Url!, path, progress, cancellationToken);

                var checksum = string.IsNullOrEmpty(link.ChecksumUrl)
                    ? null
                    : await ReadChecksumAsync(path, link.ChecksumUrl, cancellationToken);

                CheckSize(path, checksum?.Size ?? 0, expectedSize);

                if (verify && !string.IsNullOrWhiteSpace(checksum?.Md5))
                    await VerifyAsync(path, checksum.Md5, cancellationToken);

                return path;
            }
            catch (Exception exception) when (attempt < MaxAttempts && IsTransient(exception, cancellationToken))
            {
                var delay = RetryDelay(attempt);

                logger.LogWarning(
                    "Downloading {File} failed ({Message}); retrying in {Delay}s",
                    Path.GetFileName(path), exception.Message, delay.TotalSeconds);

                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    /// <summary>The file name a CDN address ends in.</summary>
    public static string FileName(string url)
    {
        var segment = Uri.UnescapeDataString(new Uri(url).Segments[^1]).Trim('/');
        var name = string.Join('_', segment.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));

        if (name.Length == 0 || name is "." or "..")
            throw new GogApiException($"Could not tell what to name the file at {new Uri(url).Host}.");

        return name;
    }

    private async Task DownloadToAsync(string url, string path, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var partial = path + ".part";

        if (File.Exists(path))
        {
            // Finished on an earlier attempt or an earlier run.
            progress?.Report(new FileInfo(path).Length);

            return;
        }

        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (offset > 0)
            request.Headers.Range = new RangeHeaderValue(offset, null);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The partial file is already whole.
            File.Move(partial, path, overwrite: true);
            progress?.Report(offset);

            return;
        }

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"GOG's CDN answered {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);

        // A server that ignores the range starts again from the top.
        var append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;

        if (!append)
            offset = 0;

        await using (var output = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            var buffer = new byte[1 << 16];
            var written = offset;
            var reported = Environment.TickCount64;

            progress?.Report(written);

            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);

                if (read == 0)
                    break;

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

                written += read;

                if (Environment.TickCount64 - reported >= 250)
                {
                    progress?.Report(written);
                    reported = Environment.TickCount64;
                }
            }

            progress?.Report(written);
        }

        File.Move(partial, path, overwrite: true);
    }

    /// <summary>What GOG's checksum XML says about a file.</summary>
    private sealed record Checksum(string? Md5, long Size);

    private async Task<Checksum?> ReadChecksumAsync(string path, string checksumUrl, CancellationToken cancellationToken)
    {
        try
        {
            var xml = await http.GetStringAsync(checksumUrl, cancellationToken);
            var root = XDocument.Parse(xml).Root;

            return new Checksum(
                root?.Attribute("md5")?.Value,
                long.TryParse(root?.Attribute("total_size")?.Value, out var size) ? size : 0);
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Xml.XmlException)
        {
            // Older products publish no checksum. Nothing to compare against is not a failure.
            logger.LogInformation("No checksum available for {File}: {Message}", Path.GetFileName(path), exception.Message);

            return null;
        }
    }

    /// <summary>
    /// Checks the file's length against the exact size from the checksum XML when there is one,
    /// otherwise against the size the products API lists.
    /// </summary>
    /// <remarks>
    /// The products API rounds sizes to whole mebibytes for some files — a 788 KB installer is
    /// listed as 1,048,576 bytes — so a listed size that is a whole number of mebibytes is only
    /// held to within one.
    /// </remarks>
    private static void CheckSize(string path, long exactSize, long listedSize)
    {
        const long MiB = 1 << 20;

        var length = new FileInfo(path).Length;

        var mismatch = exactSize > 0
            ? length != exactSize
            : listedSize > 0 && (listedSize % MiB == 0
                ? Math.Abs(length - listedSize) >= MiB
                : length != listedSize);

        if (!mismatch)
            return;

        File.Delete(path);

        throw new GogDownloadVerificationException(
            $"{Path.GetFileName(path)} is {length:N0} bytes, but GOG lists it as {(exactSize > 0 ? exactSize : listedSize):N0}.");
    }

    private static async Task VerifyAsync(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);

        var actual = Convert.ToHexStringLower(await MD5.HashDataAsync(stream, cancellationToken));

        if (!string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            stream.Close();
            File.Delete(path);

            throw new GogDownloadVerificationException(
                $"{Path.GetFileName(path)} does not match the checksum GOG publishes for it.");
        }
    }

    private static bool IsTransient(Exception exception, CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested && exception switch
        {
            HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone } => false,
            HttpRequestException => true,
            IOException => true,
            TaskCanceledException => true, // HttpClient timeout
            GogDownloadVerificationException => true, // the bad file was deleted; fetch it again
            _ => false,
        };
}
