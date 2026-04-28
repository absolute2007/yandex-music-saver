using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace YandexMusicSaver;

public class DownloadProgressEventArgs : EventArgs
{
    public double? Percentage { get; set; }
    public string? StatusText { get; set; }
    public string? TrackTitle { get; set; }
    public string? DetailsText { get; set; }
    public string? ThumbnailUrl { get; set; }
}

public class DownloadEngine
{
    public event EventHandler<DownloadProgressEventArgs>? ProgressChanged;
    public event EventHandler<string>? LogMessage;

    // Matches yt-dlp output like "[download]  45.0% of   3.20MiB at    1.12MiB/s ETA 00:01"
    private static readonly Regex ProgressRegex = new Regex(@"\[download\]\s+(?<percent>[\d\.]+)%\s+of\s+~?\s*(?<size>[^\s]+)(?:\s+at\s+(?<speed>[^\s]+))?(?:\s+ETA\s+(?<eta>[\d:]+))?", RegexOptions.Compiled);
    private static readonly Regex DestinationRegex = new Regex(@"\[download\]\s+Destination:\s+(.+)", RegexOptions.Compiled);
    private static readonly Regex ExistingFileRegex = new Regex(@"\[download\]\s+(.+)\s+has already been downloaded", RegexOptions.Compiled);
    private static readonly Regex PostProcessorDestinationRegex = new Regex(@"\[(?:ExtractAudio|EmbedThumbnail|Metadata)\]\s+Destination:\s+(.+)", RegexOptions.Compiled);
    private static readonly Regex DownloadingItemRegex = new Regex(@"\[download\]\s+Downloading item (?<current>\d+) of (?<total>\d+)", RegexOptions.Compiled);
    private static readonly Regex SharedPlaylistRegex = new Regex(@"^https?://music\.yandex\.(?:ru|kz|ua|by|com)/playlists/[^/?#]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PlaylistTrackRegex = new Regex(@"""id"":""(?<track>\d+)""\s*,\s*""albumId"":(?<album>\d+)", RegexOptions.Compiled);
    private const string ThumbnailPrefix = "YMS_THUMBNAIL:";
    private static readonly HttpClient HttpClient = new();

    public async Task DownloadAsync(System.Collections.Generic.List<string> urls, string outputDirectory, string browserCookies, CancellationToken cancellationToken)
    {
        ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
        {
            Percentage = 0,
            StatusText = "Получение данных...",
            DetailsText = "Проверка ссылок"
        });

        urls = await ResolveSharedPlaylistUrlsAsync(urls, cancellationToken);

        var arguments = new System.Collections.Generic.List<string>
        {
            "--newline",
            "--sleep-requests", "1",
            "--sleep-interval", "2",
            "--max-sleep-interval", "5",
            "-x",
            "--audio-format", "mp3",
            "--audio-quality", "0",
            "--embed-metadata",
            "--embed-thumbnail",
            "--print", $"before_dl:{ThumbnailPrefix}%(thumbnail)s"
        };
        
        if (!string.IsNullOrWhiteSpace(browserCookies) &&
            !browserCookies.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("--cookies-from-browser");
            arguments.Add(browserCookies);
        }

        // Setup output template
        arguments.Add("-P");
        arguments.Add(outputDirectory);
        arguments.Add("-o");
        arguments.Add("%(artist)s - %(album)s/%(playlist_index)s - %(title)s.%(ext)s");
        
        foreach(var url in urls)
        {
            arguments.Add(url);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "yt-dlp",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        LogMessage?.Invoke(this, $"Запуск загрузки...\nКоманда: yt-dlp {string.Join(" ", arguments.Select(EscapeArgumentForLog))}");

        using var process = new Process { StartInfo = startInfo };

        string currentTrackTitle = "Получение данных...";
        string? lastError = null;
        int currentItem = 0;
        int totalItems = urls.Count;

        double ToOverallPercentage(double trackPercentage)
        {
            if (totalItems <= 1 || currentItem <= 0)
                return trackPercentage;

            return Math.Clamp(((currentItem - 1) + trackPercentage / 100d) / totalItems * 100d, 0, 100);
        }

        Action<string?> processLine = (data) =>
        {
            if (string.IsNullOrWhiteSpace(data)) return;

            LogMessage?.Invoke(this, data);

            if (data.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            {
                lastError = data;
                ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
                {
                    StatusText = "Ошибка загрузки",
                    DetailsText = data.Replace("ERROR:", "", StringComparison.OrdinalIgnoreCase).Trim()
                });
            }

            if (data.StartsWith(ThumbnailPrefix, StringComparison.Ordinal))
            {
                string thumbnailUrl = data[ThumbnailPrefix.Length..].Trim();
                if (thumbnailUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    thumbnailUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
                    {
                        StatusText = currentItem > 0 && totalItems > 1
                            ? $"Трек {currentItem} из {totalItems}"
                            : "Загрузка трека",
                        DetailsText = "Получение файла...",
                        TrackTitle = currentTrackTitle,
                        ThumbnailUrl = thumbnailUrl
                    });
                }
            }

            var destMatch = DestinationRegex.Match(data);
            if (destMatch.Success)
            {
                currentTrackTitle = destMatch.Groups[1].Value.Trim();
                ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
                {
                    StatusText = "Загрузка трека",
                    TrackTitle = currentTrackTitle
                });
            }

            var existingFileMatch = ExistingFileRegex.Match(data);
            if (existingFileMatch.Success)
            {
                currentTrackTitle = existingFileMatch.Groups[1].Value.Trim();
                ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
                {
                    Percentage = ToOverallPercentage(100),
                    StatusText = "Файл уже скачан",
                    DetailsText = "Переход к следующему треку",
                    TrackTitle = currentTrackTitle
                });
            }

            var postProcessorMatch = PostProcessorDestinationRegex.Match(data);
            if (postProcessorMatch.Success)
            {
                currentTrackTitle = postProcessorMatch.Groups[1].Value.Trim();
                ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
                {
                    StatusText = "Обработка файла",
                    DetailsText = "Сохранение MP3",
                    TrackTitle = currentTrackTitle
                });
            }

            var itemMatch = DownloadingItemRegex.Match(data);
            if (itemMatch.Success)
            {
                currentItem = int.Parse(itemMatch.Groups["current"].Value, System.Globalization.CultureInfo.InvariantCulture);
                totalItems = int.Parse(itemMatch.Groups["total"].Value, System.Globalization.CultureInfo.InvariantCulture);

                ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
                {
                    Percentage = ToOverallPercentage(0),
                    StatusText = $"Трек {currentItem} из {totalItems}",
                    DetailsText = "Получение файла..."
                });
            }

            if (data.Contains(": Downloading track JSON", StringComparison.Ordinal) ||
                data.Contains(": Downloading m3u8 information", StringComparison.Ordinal) ||
                data.Contains(": Downloading webpage", StringComparison.Ordinal))
            {
                ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
                {
                    StatusText = currentItem > 0 && totalItems > 1
                        ? $"Трек {currentItem} из {totalItems}"
                        : "Загрузка трека",
                    DetailsText = "Получение данных..."
                });
            }

            var match = ProgressRegex.Match(data);
            if (match.Success && TryReadPercent(match.Groups["percent"].Value, out double percent))
            {
                string speed = match.Groups["speed"].Success ? match.Groups["speed"].Value : "";
                string eta = match.Groups["eta"].Success ? match.Groups["eta"].Value : "";
                string size = match.Groups["size"].Value;
                
                ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
                {
                    Percentage = ToOverallPercentage(percent),
                    StatusText = totalItems > 1 && currentItem > 0
                        ? $"Трек {currentItem} из {totalItems} • {percent:F1}%"
                        : $"{percent:F1}% из {size}",
                    DetailsText = string.IsNullOrWhiteSpace(speed)
                        ? "Загрузка..."
                        : string.IsNullOrWhiteSpace(eta)
                            ? speed
                            : $"{speed} • осталось: {eta}",
                    TrackTitle = currentTrackTitle
                });
            }
        };

        process.Start();
        var outputTask = ReadProcessStreamAsync(process.StandardOutput, processLine, cancellationToken);
        var errorTask = ReadProcessStreamAsync(process.StandardError, processLine, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);

            if (process.ExitCode == 0)
            {
                DeleteLeftoverThumbnails(outputDirectory);
                ProgressChanged?.Invoke(this, new DownloadProgressEventArgs { Percentage = 100, StatusText = "Загрузка завершена" });
                LogMessage?.Invoke(this, "Загрузка успешно завершена.");
            }
            else
            {
                ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
                {
                    Percentage = 0,
                    StatusText = $"Ошибка (код: {process.ExitCode})",
                    DetailsText = lastError?.Replace("ERROR:", "", StringComparison.OrdinalIgnoreCase).Trim()
                });
                LogMessage?.Invoke(this, $"Процесс завершился с кодом {process.ExitCode}");
            }
        }
        catch (TaskCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
            LogMessage?.Invoke(this, "Загрузка отменена пользователем.");
            ProgressChanged?.Invoke(this, new DownloadProgressEventArgs { Percentage = 0, StatusText = "Отменено" });
        }
    }

    private static string EscapeArgumentForLog(string argument)
    {
        return argument.Contains(' ') ? $"\"{argument}\"" : argument;
    }

    private static async Task ReadProcessStreamAsync(StreamReader reader, Action<string?> processLine, CancellationToken cancellationToken)
    {
        var buffer = new char[1];
        var line = new System.Text.StringBuilder();

        while (await reader.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) > 0)
        {
            char current = buffer[0];
            if (current is '\r' or '\n')
            {
                if (line.Length > 0)
                {
                    processLine(line.ToString());
                    line.Clear();
                }

                continue;
            }

            line.Append(current);
        }

        if (line.Length > 0)
            processLine(line.ToString());
    }

    private async Task<System.Collections.Generic.List<string>> ResolveSharedPlaylistUrlsAsync(System.Collections.Generic.List<string> urls, CancellationToken cancellationToken)
    {
        var resolvedUrls = new System.Collections.Generic.List<string>();

        foreach (var url in urls)
        {
            if (!SharedPlaylistRegex.IsMatch(url))
            {
                resolvedUrls.Add(url);
                continue;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0");

                using var response = await HttpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                string html = await response.Content.ReadAsStringAsync(cancellationToken);
                var trackUrls = PlaylistTrackRegex.Matches(html)
                    .Select(match => $"https://music.yandex.ru/album/{match.Groups["album"].Value}/track/{match.Groups["track"].Value}")
                    .Distinct()
                    .ToList();

                if (trackUrls.Count > 0)
                {
                    LogMessage?.Invoke(this, $"Плейлист распознан: найдено треков {trackUrls.Count}");
                    resolvedUrls.AddRange(trackUrls);
                    continue;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                LogMessage?.Invoke(this, $"Не удалось разобрать ссылку плейлиста: {ex.Message}");
            }

            resolvedUrls.Add(url);
        }

        return resolvedUrls;
    }

    private static bool TryReadPercent(string value, out double percent)
    {
        return double.TryParse(
            value.Trim().TrimEnd('%'),
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out percent);
    }

    private static void DeleteLeftoverThumbnails(string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
            return;

        foreach (var file in Directory.EnumerateFiles(outputDirectory, "*.*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is ".jpg" or ".jpeg" or ".png" or ".webp")
            {
                try { File.Delete(file); } catch { }
            }
        }
    }
}
