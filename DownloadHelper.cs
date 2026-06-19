using System.Net;
using System.Net.Http.Headers;
using Downloader;
using ShiroBot.SDK.Abstractions;

namespace ShiroBot.Plugin.FileLink;

internal static class DownloadHelper
{
    private const int DownloadParts = 10;
    private const int DownloadRetries = 5;

    public static async Task<DownloadMetadata> GetDownloadMetadataAsync(
        Uri url,
        string? httpProxy,
        CancellationToken cancellationToken)
    {
        using var httpClient = CreateHttpClient(httpProxy);
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var supportsRanges = response.Headers.AcceptRanges.Contains("bytes", StringComparer.OrdinalIgnoreCase);
        if (supportsRanges && response.Content.Headers.ContentLength is > 0)
        {
            supportsRanges = await SupportsRangeProbeAsync(httpClient, url, cancellationToken);
        }

        return new DownloadMetadata(
            url,
            response.Content.Headers.ContentLength,
            supportsRanges,
            GetFileNameFromContentDisposition(response.Content.Headers.ContentDisposition) ?? GetFileNameFromUri(response.RequestMessage?.RequestUri));
    }

    public static async Task DownloadWithProgressAsync(
        Uri url,
        string destinationPath,
        DownloadMetadata metadata,
        string fileName,
        string? httpProxy,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        DeleteIfExists(destinationPath);
        DeleteIfExists($"{destinationPath}.download");

        var preferParallelDownload = metadata.TotalBytes is > 0 && DownloadParts > 1;
        BotLog.Info(preferParallelDownload
            ? $"下载模式: Downloader 优先并发分片 ({DownloadParts} parts)，服务端声明 Range={metadata.SupportsRanges}"
            : "下载模式: Downloader 单线程");

        var proxy = CreateProxy(httpProxy);
        var configuration = new DownloadConfiguration
        {
            ChunkCount = preferParallelDownload ? DownloadParts : 1,
            ParallelCount = preferParallelDownload ? DownloadParts : 1,
            ParallelDownload = preferParallelDownload,
            MaxTryAgainOnFailure = DownloadRetries,
            BufferBlockSize = 10240,
            MinimumSizeOfChunking = 1024 * 1024,
            MinimumChunkSize = 1024 * 1024,
            CheckDiskSizeBeforeDownload = false,
            ClearPackageOnCompletionWithFailure = true,
            EnableAutoResumeDownload = false,
            DownloadFileExtension = ".download",
            FileExistPolicy = FileExistPolicy.Delete,
            RequestConfiguration =
            {
                Accept = "*/*",
                KeepAlive = true,
                ProtocolVersion = HttpVersion.Version11,
                Proxy = proxy,
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0 Safari/537.36"
            }
        };

        using var downloader = new DownloadService(configuration);
        var progressSync = new object();
        var lastLoggedAt = DateTimeOffset.MinValue;

        downloader.DownloadStarted += (_, e) =>
        {
            BotLog.Info($"开始下载 {e.FileName}，总大小 {FormatSize(e.TotalBytesToReceive)}");
        };

        downloader.DownloadProgressChanged += (_, e) =>
        {
            lock (progressSync)
            {
                var now = DateTimeOffset.UtcNow;
                if (now - lastLoggedAt < TimeSpan.FromSeconds(5) && e.ProgressPercentage < 100d)
                {
                    return;
                }

                lastLoggedAt = now;
                var bytesPerSecond = e.BytesPerSecondSpeed > 0d ? e.BytesPerSecondSpeed : e.AverageBytesPerSecondSpeed;
                if (e.TotalBytesToReceive > 0)
                {
                    BotLog.Info(
                        $"下载进度 {fileName}: {FormatSize(e.ReceivedBytesSize)} / {FormatSize(e.TotalBytesToReceive)} ({e.ProgressPercentage:F1}%) 速度 {FormatSize((long)bytesPerSecond)}/s");
                }
                else
                {
                    BotLog.Info(
                        $"下载进度 {fileName}: {FormatSize(e.ReceivedBytesSize)} 速度 {FormatSize((long)bytesPerSecond)}/s");
                }
            }
        };

        await downloader.DownloadFileTaskAsync(url.ToString(), destinationPath, cancellationToken);
    }

    private static string? GetFileNameFromContentDisposition(ContentDispositionHeaderValue? contentDisposition)
    {
        var fileName = contentDisposition?.FileNameStar ?? contentDisposition?.FileName;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        return fileName.Trim().Trim('"');
    }

    private static string? GetFileNameFromUri(Uri? uri)
    {
        if (uri is null)
        {
            return null;
        }

        var fileName = Path.GetFileName(Uri.UnescapeDataString(uri.LocalPath.TrimEnd('/')));
        return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
    }

    private static async Task<bool> SupportsRangeProbeAsync(
        HttpClient httpClient,
        Uri url,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return response.StatusCode == HttpStatusCode.PartialContent;
    }

    private static HttpClient CreateHttpClient(string? httpProxy)
    {
        var proxy = CreateProxy(httpProxy);
        var handler = new HttpClientHandler
        {
            UseProxy = proxy is not null,
            Proxy = proxy
        };

        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static WebProxy? CreateProxy(string? httpProxy)
    {
        if (string.IsNullOrWhiteSpace(httpProxy))
        {
            return null;
        }

        if (!Uri.TryCreate(httpProxy.Trim(), UriKind.Absolute, out var proxyUri))
        {
            throw new ArgumentException($"HTTP 代理地址格式错误: {httpProxy}");
        }

        return new WebProxy(proxyUri);
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }
}

internal sealed record DownloadMetadata(
    Uri Url,
    long? TotalBytes,
    bool SupportsRanges,
    string? SuggestedFileName);
