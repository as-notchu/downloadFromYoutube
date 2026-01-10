using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using FFMpegCore;
using FFMpegEnums = FFMpegCore.Enums;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Videos.Streams;

namespace DownloadFY;

public class MainService
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly Channel<QueueItem> _queue = Channel.CreateUnbounded<QueueItem>();
    private readonly ILogger<MainService> _logger;
    private int _queuedCount = 0;
    private readonly YoutubeClient _youtubeClient;
    private readonly Dictionary<Guid, TaskResult> _taskResults = new();
    private int _downloadCount = 0;

    public MainService(ILogger<MainService> logger, YoutubeClient youtubeClient)
    {
        _logger = logger;
        _youtubeClient = youtubeClient;
        _ = ProcessQueueAsync();
    }

    public async Task<IResult> DoSmth(string[] values)
    {
        var taskId = Guid.NewGuid();
        var queueItem = new QueueItem(taskId, values, DownloadType.AudioPlaylist);

        var currentPosition = Interlocked.Increment(ref _queuedCount);

        lock (_taskResults)
        {
            _taskResults[taskId] = new TaskResult
            {
                Status = TaskStatus.Queued,
                QueuedAt = DateTime.UtcNow
            };
        }

        await _queue.Writer.WriteAsync(queueItem);
        _logger.LogInformation("Task {TaskId} added to queue at position {Position} with {Count} values",
            taskId, currentPosition, values?.Length ?? 0);

        return Results.Ok(new
        {
            taskId,
            status = "queued",
            queuePosition = currentPosition,
            queuedAt = DateTime.UtcNow
        });
    }

    public async Task<IResult> DownloadVideos(string[] videoUrls)
    {
        var taskId = Guid.NewGuid();
        var queueItem = new QueueItem(taskId, videoUrls, DownloadType.Video);

        var currentPosition = Interlocked.Increment(ref _queuedCount);

        lock (_taskResults)
        {
            _taskResults[taskId] = new TaskResult
            {
                Status = TaskStatus.Queued,
                QueuedAt = DateTime.UtcNow
            };
        }

        await _queue.Writer.WriteAsync(queueItem);
        _logger.LogInformation("Video download task {TaskId} added to queue at position {Position} with {Count} videos",
            taskId, currentPosition, videoUrls?.Length ?? 0);

        return Results.Ok(new
        {
            taskId,
            status = "queued",
            queuePosition = currentPosition,
            queuedAt = DateTime.UtcNow
        });
    }

    public async Task<IResult> DownloadSingleAudio(string videoUrl)
    {
        var taskId = Guid.NewGuid();
        var queueItem = new QueueItem(taskId, new[] { videoUrl }, DownloadType.SingleAudio);

        var currentPosition = Interlocked.Increment(ref _queuedCount);

        lock (_taskResults)
        {
            _taskResults[taskId] = new TaskResult
            {
                Status = TaskStatus.Queued,
                QueuedAt = DateTime.UtcNow
            };
        }

        await _queue.Writer.WriteAsync(queueItem);
        _logger.LogInformation("Single audio download task {TaskId} added to queue at position {Position}",
            taskId, currentPosition);

        return Results.Ok(new
        {
            taskId,
            status = "queued",
            queuePosition = currentPosition,
            queuedAt = DateTime.UtcNow
        });
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync())
        {
            try
            {
                await _semaphore.WaitAsync();

                lock (_taskResults)
                {
                    if (_taskResults.TryGetValue(item.TaskId, out var taskResult))
                    {
                        taskResult.Status = TaskStatus.Processing;
                        taskResult.StartedAt = DateTime.UtcNow;
                    }
                }

                _logger.LogInformation("Processing task {TaskId} with {Count} playlists",
                    item.TaskId, item.Values?.Length ?? 0);

                if (item.Values == null || item.Values.Length == 0)
                {
                    throw new ArgumentException("Values array is empty");
                }

                var taskDirectory = Path.Combine(Directory.GetCurrentDirectory(), item.TaskId.ToString());
                Directory.CreateDirectory(taskDirectory);

                if (item.Type == DownloadType.AudioPlaylist)
                {
                    for (int i = 0; i < item.Values.Length; i++)
                    {
                        var playlistUrl = item.Values[i];
                        if (string.IsNullOrEmpty(playlistUrl))
                        {
                            _logger.LogWarning("Skipping null/empty playlist at index {Index} for task {TaskId}", i, item.TaskId);
                            continue;
                        }

                        _logger.LogInformation("Processing playlist {Index}/{Total} for task {TaskId}: {Url}",
                            i + 1, item.Values.Length, item.TaskId, playlistUrl);

                        await ProcessPlaylist(playlistUrl, taskDirectory);
                    }
                }
                else if (item.Type == DownloadType.Video)
                {
                    _logger.LogInformation("Processing {Count} videos for task {TaskId}", item.Values.Length, item.TaskId);
                    await ProcessVideos(item.Values, taskDirectory);
                }
                else if (item.Type == DownloadType.SingleAudio)
                {
                    _logger.LogInformation("Processing single audio for task {TaskId}", item.TaskId);
                    await ProcessSingleAudio(item.Values[0], taskDirectory);
                }

                var result = $"Task {item.TaskId} completed successfully with {item.Values?.Length ?? 0} values!";

                lock (_taskResults)
                {
                    if (_taskResults.TryGetValue(item.TaskId, out var taskResult))
                    {
                        taskResult.Status = TaskStatus.Completed;
                        taskResult.CompletedAt = DateTime.UtcNow;
                        taskResult.Message = result;
                    }
                }

                item.CompletionSource.SetResult(result);
                _logger.LogInformation("Task {TaskId} completed", item.TaskId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing task {TaskId}", item.TaskId);

                lock (_taskResults)
                {
                    if (_taskResults.TryGetValue(item.TaskId, out var taskResult))
                    {
                        taskResult.Status = TaskStatus.Failed;
                        taskResult.CompletedAt = DateTime.UtcNow;
                        taskResult.Error = ex.Message;
                    }
                }

                item.CompletionSource.SetException(ex);
            }
            finally
            {
                _semaphore.Release();
                Interlocked.Decrement(ref _queuedCount);
            }
        }
    }

    private async Task ProcessPlaylist(string playlistUrl, string taskDirectory)
    {
        var playlist = await _youtubeClient.Playlists.GetAsync(playlistUrl);
        
        var playlistId = playlist.Id.Value;

        var playlistDirectory = Path.Combine(taskDirectory, playlistId);
        
        Directory.CreateDirectory(playlistDirectory);

        _logger.LogInformation("Downloading playlist '{Title}' (ID: {PlaylistId}) to {Directory}",
            playlist.Title, playlistId, playlistDirectory);

        var videos = await _youtubeClient.Playlists.GetVideosAsync(playlistUrl);

        if (videos.Count <= 0)
        {
            _logger.LogWarning("Playlist {PlaylistId} is empty", playlistId);
            return;
        }

        _logger.LogInformation("Found {Count} videos in playlist {PlaylistId}", videos.Count, playlistId);

        var fileCounter = new Dictionary<string, int>();

        foreach (var video in videos)
        {
            StreamManifest manifest;
            try
            {
                manifest = await _youtubeClient.Videos.Streams.GetManifestAsync(video.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get manifest for video {VideoId} in playlist {PlaylistId}", video.Id, playlistId);
                continue;
            }
            if (manifest is null) continue;

            IStreamInfo stream;
            try
            {
                stream = GetAudioStream(manifest);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get audio stream for video {VideoId} in playlist {PlaylistId}", video.Id, playlistId);
                continue;
            }

            if (stream is null) continue;

            var sanitizedTitle = SanitizeFileName(video.Title);
            
            if (fileCounter.ContainsKey(sanitizedTitle))
            {
                fileCounter[sanitizedTitle]++;
                sanitizedTitle = $"{sanitizedTitle}_{fileCounter[sanitizedTitle]}";
            }
            else
            {
                fileCounter[sanitizedTitle] = 0;
            }

            var path = Path.Combine(playlistDirectory, $"{sanitizedTitle}.{stream.Container}");
            _logger.LogInformation("Downloading: {Title} -> {FileName}", video.Title, sanitizedTitle);

            try
            {
                await _youtubeClient.Videos.Streams.DownloadAsync(stream, path);
                var currentDownloadCount = Interlocked.Increment(ref _downloadCount);
                
                if (currentDownloadCount >= 25)
                {
                    _logger.LogInformation("Rate limit reached ({Count} downloads), waiting 120 seconds", currentDownloadCount);
                    await Task.Delay(TimeSpan.FromSeconds(120));
                    Interlocked.Exchange(ref _downloadCount, 0);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download video {VideoId}: {Title}", video.Id, video.Title);
            }

            await Task.Delay(TimeSpan.FromSeconds(10));
        }

        _logger.LogInformation("Completed downloading playlist {PlaylistId}", playlistId);
    }

    private async Task ProcessVideos(string[] videoUrls, string taskDirectory)
    {
        _logger.LogInformation("Downloading {Count} videos to {Directory}", videoUrls.Length, taskDirectory);

        var fileCounter = new Dictionary<string, int>();

        foreach (var videoUrl in videoUrls)
        {
            try
            {
                var video = await _youtubeClient.Videos.GetAsync(videoUrl);

                _logger.LogInformation("Processing video: {Title} (ID: {VideoId})", video.Title, video.Id);

                StreamManifest manifest;
                try
                {
                    manifest = await _youtubeClient.Videos.Streams.GetManifestAsync(video.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get manifest for video {VideoId}", video.Id);
                    continue;
                }

                if (manifest is null)
                {
                    _logger.LogWarning("Manifest is null for video {VideoId}", video.Id);
                    continue;
                }

                IStreamInfo videoStream;
                IStreamInfo audioStream;

                try
                {
                    videoStream = GetVideoStream(manifest);
                    audioStream = GetAudioStream(manifest);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get streams for video {VideoId}", video.Id);
                    continue;
                }

                if (videoStream is null || audioStream is null)
                {
                    _logger.LogWarning("Video or audio stream is null for video {VideoId}", video.Id);
                    continue;
                }

                var sanitizedTitle = SanitizeFileName(video.Title);

                if (fileCounter.ContainsKey(sanitizedTitle))
                {
                    fileCounter[sanitizedTitle]++;
                    sanitizedTitle = $"{sanitizedTitle}_{fileCounter[sanitizedTitle]}";
                }
                else
                {
                    fileCounter[sanitizedTitle] = 0;
                }

                var videoTempPath = Path.Combine(taskDirectory, $"{sanitizedTitle}_video.{videoStream.Container}");
                var audioTempPath = Path.Combine(taskDirectory, $"{sanitizedTitle}_audio.{audioStream.Container}");
                var outputPath = Path.Combine(taskDirectory, $"{sanitizedTitle}.mp4");

                _logger.LogInformation("Downloading video stream: {Title}", video.Title);

                try
                {
                    await _youtubeClient.Videos.Streams.DownloadAsync(videoStream, videoTempPath);
                    Interlocked.Increment(ref _downloadCount);
                    _logger.LogInformation("Video stream downloaded: {Title}", video.Title);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to download video stream {VideoId}: {Title}", video.Id, video.Title);
                    continue;
                }

                _logger.LogInformation("Downloading audio stream: {Title}", video.Title);

                try
                {
                    await _youtubeClient.Videos.Streams.DownloadAsync(audioStream, audioTempPath);
                    Interlocked.Increment(ref _downloadCount);
                    _logger.LogInformation("Audio stream downloaded: {Title}", video.Title);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to download audio stream {VideoId}: {Title}", video.Id, video.Title);

                    if (File.Exists(videoTempPath))
                    {
                        File.Delete(videoTempPath);
                    }
                    continue;
                }

                _logger.LogInformation("Merging video and audio streams: {Title}", video.Title);

                try
                {
                    await MergeStreamsAsync(videoTempPath, audioTempPath, outputPath);
                    _logger.LogInformation("Successfully merged streams: {Title}", video.Title);

                    if (File.Exists(videoTempPath))
                    {
                        File.Delete(videoTempPath);
                        _logger.LogInformation("Deleted temporary video file: {Path}", videoTempPath);
                    }

                    if (File.Exists(audioTempPath))
                    {
                        File.Delete(audioTempPath);
                        _logger.LogInformation("Deleted temporary audio file: {Path}", audioTempPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to merge streams for video {VideoId}: {Title}", video.Id, video.Title);

                    if (File.Exists(videoTempPath))
                    {
                        File.Delete(videoTempPath);
                    }
                    if (File.Exists(audioTempPath))
                    {
                        File.Delete(audioTempPath);
                    }
                    continue;
                }

                await Task.Delay(TimeSpan.FromSeconds(10));

                var currentDownloadCount = _downloadCount;
                if (currentDownloadCount >= 25)
                {
                    _logger.LogInformation("Rate limit reached ({Count} downloads), waiting 120 seconds", currentDownloadCount);
                    await Task.Delay(TimeSpan.FromSeconds(120));
                    Interlocked.Exchange(ref _downloadCount, 0);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing video URL: {Url}", videoUrl);
            }
        }

        _logger.LogInformation("Completed downloading videos");
    }

    private async Task ProcessSingleAudio(string videoUrl, string taskDirectory)
    {
        _logger.LogInformation("Downloading single audio from URL: {Url}", videoUrl);

        try
        {
            var video = await _youtubeClient.Videos.GetAsync(videoUrl);

            _logger.LogInformation("Processing video: {Title} (ID: {VideoId})", video.Title, video.Id);

            StreamManifest manifest;
            try
            {
                manifest = await _youtubeClient.Videos.Streams.GetManifestAsync(video.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get manifest for video {VideoId}", video.Id);
                throw;
            }

            if (manifest is null)
            {
                throw new Exception($"Manifest is null for video {video.Id}");
            }

            IStreamInfo audioStream;
            try
            {
                audioStream = GetAudioStream(manifest);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get audio stream for video {VideoId}", video.Id);
                throw;
            }

            if (audioStream is null)
            {
                throw new Exception($"Audio stream is null for video {video.Id}");
            }

            var sanitizedTitle = SanitizeFileName(video.Title);
            var outputPath = Path.Combine(taskDirectory, $"{sanitizedTitle}.{audioStream.Container}");

            _logger.LogInformation("Downloading audio: {Title} -> {FileName}", video.Title, sanitizedTitle);

            await _youtubeClient.Videos.Streams.DownloadAsync(audioStream, outputPath);
            Interlocked.Increment(ref _downloadCount);

            _logger.LogInformation("Successfully downloaded single audio: {Title}", video.Title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing single audio URL: {Url}", videoUrl);
            throw;
        }
    }

    private IStreamInfo GetAudioStream(StreamManifest manifest)
    {
        return manifest.GetAudioOnlyStreams()
            .Where(x => x.Container == Container.WebM)
            .GetWithHighestBitrate();
    }

    private IStreamInfo GetVideoStream(StreamManifest manifest)
    {
        return manifest.GetVideoOnlyStreams()
            .Where(x => x.Container == Container.Mp4)
            .GetWithHighestVideoQuality();
    }

    private async Task MergeStreamsAsync(string videoPath, string audioPath, string outputPath)
    {
        await FFMpegArguments
            .FromFileInput(videoPath)
            .AddFileInput(audioPath)
            .OutputToFile(outputPath, overwrite: true, options => options
                .CopyChannel(FFMpegEnums.Channel.Video)
                .WithAudioCodec(FFMpegEnums.AudioCodec.Aac))
            .ProcessAsynchronously();
    }

    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "unnamed";
        }

        // Replace invalid path characters with underscore
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new StringBuilder(fileName.Length);

        foreach (var c in fileName)
        {
            // Keep letters (including Cyrillic, Chinese, etc.), digits, and safe punctuation
            // Replace invalid file system characters and control characters
            if (invalidChars.Contains(c) || char.IsControl(c))
            {
                sanitized.Append('_');
            }
            else if (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '.' ||
                     c == '(' || c == ')' || c == '[' || c == ']' || c == '\'' || c == ',')
            {
                sanitized.Append(c);
            }
            else
            {
                // Replace other special symbols with underscore
                sanitized.Append('_');
            }
        }

        var result = sanitized.ToString();

        // Replace multiple consecutive underscores with a single underscore
        result = Regex.Replace(result, "_+", "_");

        // Remove leading/trailing underscores and whitespace
        result = result.Trim('_', ' ', '.');

        // Handle Windows reserved names (CON, PRN, AUX, NUL, COM1-9, LPT1-9)
        var reservedNames = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
                                     "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4",
                                     "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        if (reservedNames.Contains(result.ToUpperInvariant()))
        {
            result = "_" + result;
        }

        // Ensure the filename is not empty after sanitization
        if (string.IsNullOrWhiteSpace(result))
        {
            result = "unnamed";
        }

        // Limit filename length to 200 characters (leaving room for extension)
        if (result.Length > 160)
        {
            result = result.Substring(0, 160);
        }

        return result;
    }

    public int GetQueueLength() => _queuedCount;

    public TaskResult? GetTaskStatus(Guid taskId)
    {
        lock (_taskResults)
        {
            return _taskResults.TryGetValue(taskId, out var result) ? result : null;
        }
    }

    public (bool exists, string? zipPath) CompressDirectory(Guid taskId)
    {
        var directoryPath = Path.Combine(Directory.GetCurrentDirectory(), taskId.ToString());

        if (!Directory.Exists(directoryPath))
        {
            _logger.LogWarning("Directory not found for task {TaskId}", taskId);
            return (false, null);
        }

        var zipPath = Path.Combine(Directory.GetCurrentDirectory(), $"{taskId}.zip");

        try
        {
            if (File.Exists(zipPath))
            {
                _logger.LogInformation("Zip file already exists for task {TaskId}, returning existing file", taskId);
                return (true, zipPath);
            }

            _logger.LogInformation("Creating zip file for task {TaskId}", taskId);
            System.IO.Compression.ZipFile.CreateFromDirectory(directoryPath, zipPath);
            _logger.LogInformation("Zip file created successfully for task {TaskId}", taskId);

            return (true, zipPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating zip file for task {TaskId}", taskId);
            return (false, null);
        }
    }

    public void CleanupTask(Guid taskId)
    {
        try
        {
            // Remove from task results
            lock (_taskResults)
            {
                _taskResults.Remove(taskId);
            }

            // Delete directory
            var directoryPath = Path.Combine(Directory.GetCurrentDirectory(), taskId.ToString());
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
                _logger.LogInformation("Deleted directory for task {TaskId}", taskId);
            }

            // Delete zip file
            var zipPath = Path.Combine(Directory.GetCurrentDirectory(), $"{taskId}.zip");
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
                _logger.LogInformation("Deleted zip file for task {TaskId}", taskId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up task {TaskId}", taskId);
        }
    }

    public (bool exists, string? filePath) GetSingleAudioFile(Guid taskId)
    {
        var directoryPath = Path.Combine(Directory.GetCurrentDirectory(), taskId.ToString());

        if (!Directory.Exists(directoryPath))
        {
            _logger.LogWarning("Directory not found for task {TaskId}", taskId);
            return (false, null);
        }

        try
        {
            var files = Directory.GetFiles(directoryPath);

            if (files.Length == 0)
            {
                _logger.LogWarning("No files found in directory for task {TaskId}", taskId);
                return (false, null);
            }

            // Return the first (and should be only) file
            var filePath = files[0];
            _logger.LogInformation("Found single audio file for task {TaskId}: {FilePath}", taskId, filePath);
            return (true, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting single audio file for task {TaskId}", taskId);
            return (false, null);
        }
    }

    private record QueueItem(Guid TaskId, string[] Values, DownloadType Type)
    {
        public TaskCompletionSource<string> CompletionSource { get; } = new();
    }

    public class TaskResult
    {
        public TaskStatus Status { get; set; }
        public DateTime QueuedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? Message { get; set; }
        public string? Error { get; set; }
    }

    public enum TaskStatus
    {
        Queued,
        Processing,
        Completed,
        Failed
    }

    public enum DownloadType
    {
        AudioPlaylist,
        Video,
        SingleAudio
    }
}