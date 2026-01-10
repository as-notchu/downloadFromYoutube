using DownloadFY;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Models;
using YoutubeExplode;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

builder.Services.AddSingleton<MainService>();
builder.Services.AddSingleton(new YoutubeClient());

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
  
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Enter 'Bearer' [space] and then your token",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

app.UseCors("AllowAll");

app.UseSwagger();
app.UseSwaggerUI();


app.MapPost("/do-smth", async (DoSmthRequest request, MainService service) =>
{
    // Validate request
    if (request.Values == null || request.Values.Length == 0)
    {
        return Results.BadRequest(new { error = "Values array is required and cannot be empty" });
    }

    if (request.Values.Length > 10)
    {
        return Results.BadRequest(new { error = "Maximum 10 playlists per request" });
    }

    foreach (var value in request.Values)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Results.BadRequest(new { error = "Playlist URLs cannot be empty" });
        }

        if (!value.Contains("youtube.com") && !value.Contains("youtu.be"))
        {
            return Results.BadRequest(new { error = "Only YouTube URLs are supported" });
        }
    }

    return await service.DoSmth(request.Values);
})
    .WithName("DoSomething");

app.MapPost("/download-video", async (DownloadVideoRequest request, MainService service) =>
{
    // Validate request
    if (request.VideoUrls == null || request.VideoUrls.Length == 0)
    {
        return Results.BadRequest(new { error = "VideoUrls array is required and cannot be empty" });
    }

    if (request.VideoUrls.Length > 10)
    {
        return Results.BadRequest(new { error = "Maximum 10 videos per request" });
    }

    foreach (var url in request.VideoUrls)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Results.BadRequest(new { error = "Video URLs cannot be empty" });
        }

        if (!url.Contains("youtube.com") && !url.Contains("youtu.be"))
        {
            return Results.BadRequest(new { error = "Only YouTube URLs are supported" });
        }
    }

    return await service.DownloadVideos(request.VideoUrls);
})
    .WithName("DownloadVideo");

app.MapPost("/download-single-audio", async (DownloadSingleAudioRequest request, MainService service) =>
{
    // Validate request
    if (string.IsNullOrWhiteSpace(request.VideoUrl))
    {
        return Results.BadRequest(new { error = "VideoUrl is required and cannot be empty" });
    }

    if (!request.VideoUrl.Contains("youtube.com") && !request.VideoUrl.Contains("youtu.be"))
    {
        return Results.BadRequest(new { error = "Only YouTube URLs are supported" });
    }

    return await service.DownloadSingleAudio(request.VideoUrl);
})
    .WithName("DownloadSingleAudio");


app.MapGet("/queue/status", (MainService service) => Results.Ok(new
    {
        queueLength = service.GetQueueLength(),
        timestamp = DateTime.UtcNow
    }))
    .WithName("GetQueueStatus");

app.MapGet("/data/{id}/download", async (MainService service, Guid id, HttpContext context) =>
{
    var taskResult = service.GetTaskStatus(id);

    if (taskResult == null)
    {
        return Results.NotFound(new { error = "Task not found" });
    }

    if (taskResult.Status != MainService.TaskStatus.Completed)
    {
        return Results.BadRequest(new
        {
            error = "Task is not completed yet",
            currentStatus = taskResult.Status.ToString().ToLower()
        });
    }

    var (exists, zipPath) = service.CompressDirectory(id);

    if (!exists || zipPath == null)
    {
        return Results.NotFound(new { error = "Directory not found or could not be compressed" });
    }

    // Stream the file in chunks to avoid loading entire file into memory
    var fileInfo = new FileInfo(zipPath);
    context.Response.ContentType = "application/zip";
    context.Response.Headers.ContentDisposition = $"attachment; filename=\"{id}.zip\"";
    context.Response.ContentLength = fileInfo.Length;

    await using var fileStream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 10 * 1024 * 1024);
    await fileStream.CopyToAsync(context.Response.Body);

    return Results.Empty;
})
    .WithName("DownloadTaskResult");

app.MapGet("/data/{id}/download-single", async (MainService service, Guid id, HttpContext context) =>
{
    var taskResult = service.GetTaskStatus(id);

    if (taskResult == null)
    {
        return Results.NotFound(new { error = "Task not found" });
    }

    if (taskResult.Status != MainService.TaskStatus.Completed)
    {
        return Results.BadRequest(new
        {
            error = "Task is not completed yet",
            currentStatus = taskResult.Status.ToString().ToLower()
        });
    }

    var (exists, filePath) = service.GetSingleAudioFile(id);

    if (!exists || filePath == null)
    {
        return Results.NotFound(new { error = "Audio file not found" });
    }

    // Stream the file directly
    var fileInfo = new FileInfo(filePath);
    var fileName = Path.GetFileName(filePath);
    var mimeType = fileName.EndsWith(".webm") ? "audio/webm" : "application/octet-stream";

    context.Response.ContentType = mimeType;
    context.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
    context.Response.ContentLength = fileInfo.Length;

    await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 10 * 1024 * 1024);
    await fileStream.CopyToAsync(context.Response.Body);

    return Results.Empty;
})
    .WithName("SingleAudioTaskStatus");

app.MapDelete("/data/{id}", (MainService service, Guid id) =>
{
    var taskResult = service.GetTaskStatus(id);

    if (taskResult == null)
    {
        return Results.NotFound(new { error = "Task not found" });
    }

    service.CleanupTask(id);

    return Results.Ok(new
    {
        message = "Task cleaned up successfully",
        taskId = id
    });
})
    .WithName("CleanupTask");

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    timestamp = DateTime.UtcNow,
    version = "1.0.0"
}))
    .WithName("HealthCheck")
    .AllowAnonymous();

app.Run();

record DoSmthRequest(string[] Values);
record DownloadVideoRequest(string[] VideoUrls);
record DownloadSingleAudioRequest(string VideoUrl);