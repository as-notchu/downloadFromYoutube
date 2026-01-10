# DownloadFY - Codebase Reference Documentation

## Project Overview

**DownloadFY** is a containerized YouTube downloader service that enables users to download YouTube content in three modes:
1. **Audio Playlists** - Download all videos from playlists as audio-only files (WebM format)
2. **Videos** - Download individual videos with merged video+audio streams (MP4 format)
3. **Single Audio** - Download a single video as audio-only (WebM format) without ZIP compression

### Technology Stack

**Backend:**
- **Framework**: ASP.NET Core 9.0 (Minimal API)
- **Language**: C# with nullable reference types enabled
- **Runtime**: .NET 9.0 SDK

**Key Dependencies:**
- `YoutubeExplode 6.5.6` - YouTube API client for fetching metadata and downloading streams
- `FFMpegCore 5.4.0` - FFmpeg wrapper for merging video/audio streams
- `Swashbuckle.AspNetCore 9.0.6` - Swagger/OpenAPI documentation

**Frontend:**
- **Type**: Vanilla HTML/CSS/JavaScript (no frameworks)
- **Web Server**: Nginx (Alpine Linux)
- **Features**: Single-page application with real-time status polling

**Infrastructure:**
- **Containerization**: Docker with multi-stage builds
- **Container Runtime**: ffmpeg installed in backend container
- **Port Mapping**:
  - Backend: Port 1000 (host) → 5000 (container)
  - Frontend: Port 1010 (host) → 80 (container)

### Project Type and Purpose

**Type**: Web-based microservice application
**Purpose**: Provide a user-friendly interface for downloading YouTube content with queue-based processing to handle multiple concurrent requests

---

## Architecture Pattern

### Pattern: Minimal API with Background Queue Processing

The application follows a **queue-based processing architecture** with the following characteristics:

1. **Queue-Based Processing** (`System.Threading.Channels`)
   - Uses unbounded channel for task queuing
   - Single-threaded processing with semaphore (max 1 concurrent task)
   - Background task processor runs continuously

2. **Task Management System**
   - Dictionary-based in-memory task tracking
   - Task states: `Queued → Processing → Completed/Failed`
   - Frontend uses LocalStorage for task resumption across page reloads

3. **Service Layer**
   - Singleton `MainService` manages all download operations
   - Singleton `YoutubeClient` for YouTube API interactions
   - Dependency injection via ASP.NET Core DI container

4. **API Design**
   - RESTful minimal API endpoints
   - CORS enabled (AllowAll policy)
   - Swagger UI for API documentation

---

## Core Components

### 1. Main Application Entry Points

**File**: `/home/user/downloadFromYoutube/DownloadFY/Program.cs`

**Key Responsibilities:**
- Application configuration and startup (lines 6-55)
- CORS policy setup (lines 8-16)
- Service registration: MainService, YoutubeClient (lines 18-19)
- Swagger/OpenAPI configuration with Bearer auth (lines 21-48)
- API endpoint mapping (lines 58-248)

### 2. Key Modules/Services

**File**: `/home/user/downloadFromYoutube/DownloadFY/MainService.cs`

**MainService Class** - Core service managing all download operations

**Key Methods:**

| Method | Lines | Purpose |
|--------|-------|---------|
| `DoSmth()` | 29-56 | Queues audio playlist downloads |
| `DownloadVideos()` | 58-85 | Queues video downloads (video+audio merged) |
| `DownloadSingleAudio()` | 87-114 | Queues single audio downloads |
| `ProcessQueueAsync()` | 116-203 | Background queue processor (infinite loop) |
| `ProcessPlaylist()` | 205-293 | Downloads all videos from a playlist as audio |
| `ProcessVideos()` | 295-451 | Downloads videos with merged streams |
| `ProcessSingleAudio()` | 454-511 | Downloads single video as audio (no ZIP) |
| `MergeStreamsAsync()` | 562-571 | FFmpeg video/audio stream merging |
| `SanitizeFileName()` | 573-635 | Cleans filenames for filesystem compatibility |
| `CompressDirectory()` | 647-678 | Creates ZIP archive of task directory |
| `GetSingleAudioFile()` | 705-735 | Retrieves single audio file path (no ZIP) |
| `CleanupTask()` | 673-703 | Deletes task data (directory and ZIP) |

**Private Fields:**
- `_semaphore` - Limits concurrent processing to 1 task
- `_queue` - Unbounded channel for task queuing
- `_taskResults` - Dictionary tracking task status
- `_downloadCount` - Atomic counter for rate limiting

### 3. Database Models/Entities

**No Traditional Database** - The application uses in-memory storage

**Data Models** (defined in MainService.cs):

#### QueueItem (line 737)
```csharp
record QueueItem(Guid TaskId, string[] Values, DownloadType Type)
{
    public TaskCompletionSource<string> CompletionSource { get; }
}
```

#### TaskResult (lines 742-750)
```csharp
public class TaskResult
{
    public TaskStatus Status { get; set; }
    public DateTime QueuedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
}
```

#### Enums

**TaskStatus** (lines 752-757):
- `Queued` - Task added to queue
- `Processing` - Task currently being processed
- `Completed` - Task finished successfully
- `Failed` - Task encountered an error

**DownloadType** (lines 759-764):
- `AudioPlaylist` - Download playlist as audio-only files
- `Video` - Download videos with merged video+audio
- `SingleAudio` - Download single video as audio-only (NEW)

**Storage Mechanisms:**
- `Dictionary<Guid, TaskResult> _taskResults` - In-memory task tracking
- File system - Downloaded content stored in `{TaskId}/` directories
- LocalStorage (frontend) - Current task ID and download type persistence

---

## API Endpoints and Interfaces

### POST /do-smth
**File**: Program.cs (lines 58-86)
**Purpose**: Queue audio playlist downloads
**Request Body**:
```json
{
  "values": ["playlist_url_1", "playlist_url_2"]
}
```
**Validation**:
- 1-10 playlists allowed
- Must be YouTube URLs (`youtube.com` or `youtu.be`)
- URLs cannot be empty

**Response**:
```json
{
  "taskId": "guid",
  "status": "queued",
  "queuePosition": 1,
  "queuedAt": "2025-01-10T12:00:00Z"
}
```

---

### POST /download-video
**File**: Program.cs (lines 88-116)
**Purpose**: Queue video downloads with merged video+audio streams
**Request Body**:
```json
{
  "videoUrls": ["video_url_1", "video_url_2"]
}
```
**Validation**:
- 1-10 videos allowed
- Must be YouTube URLs
- URLs cannot be empty

**Response**: Same as /do-smth

---

### POST /download-single-audio
**File**: Program.cs (lines 118-133)
**Purpose**: Queue single audio download (no ZIP, direct file)
**Request Body**:
```json
{
  "videoUrl": "single_video_url"
}
```
**Validation**:
- Only one URL allowed
- Must be YouTube URL
- URL cannot be empty

**Response**: Same as /do-smth

---

### GET /queue/status
**File**: Program.cs (lines 136-141)
**Purpose**: Get current queue length
**Response**:
```json
{
  "queueLength": 2,
  "timestamp": "2025-01-10T12:00:00Z"
}
```

---

### GET /data/{id}/download
**File**: Program.cs (lines 143-179)
**Purpose**: Download completed task results as ZIP
**Returns**: ZIP file stream (10MB buffer)
**Statuses**:
- 200 - ZIP file ready, starts download
- 400 - Task not completed yet (returns current status)
- 404 - Task not found

**Behavior**: Auto-compresses task directory on first download

---

### GET /data/{id}/download-single
**File**: Program.cs (lines 181-220)
**Purpose**: Download single audio file directly (no ZIP)
**Returns**: WebM audio file stream (10MB buffer)
**Content-Type**: `audio/webm`
**Statuses**:
- 200 - Audio file ready, starts download
- 400 - Task not completed yet
- 404 - Task not found or audio file missing

---

### DELETE /data/{id}
**File**: Program.cs (lines 222-239)
**Purpose**: Cleanup task data (directory and ZIP)
**Returns**:
```json
{
  "message": "Task cleaned up successfully",
  "taskId": "guid"
}
```

---

### GET /health
**File**: Program.cs (lines 241-248)
**Purpose**: Health check endpoint
**Response**:
```json
{
  "status": "healthy",
  "timestamp": "2025-01-10T12:00:00Z",
  "version": "1.0.0"
}
```

---

## Technical Details

### Dependencies and Third-Party Libraries

**Backend** (`DownloadFY.csproj`):
```xml
<PackageReference Include="YoutubeExplode" Version="6.5.6" />
<PackageReference Include="FFMpegCore" Version="5.4.0" />
<PackageReference Include="Swashbuckle.AspNetCore" Version="9.0.6" />
```

**Container Dependencies** (Dockerfile):
- `ffmpeg` - Required for video/audio stream merging
- `tzdata` - Timezone data for date/time operations

### Configuration Files

#### docker-compose.yml
**Location**: `/home/user/downloadFromYoutube/docker-compose.yml`
**Services**:
- `backend` - ASP.NET Core API (port 1000)
- `frontend` - Nginx web server (port 1010)

**Shared Resources**:
- Network: `downloadfy-network`
- Volume: `download-data` mounted to `/app` (persistent storage)

**Restart Policy**: `always`

---

#### nginx.conf
**Location**: `/home/user/downloadFromYoutube/nginx.conf`
**Configuration**:
- Routes `/api/*` to backend service on port 5000
- Serves static files from `/usr/share/nginx/html`
- Extended timeouts (600s) for long downloads
- Proxy headers for real IP forwarding

---

#### appsettings.json
**Location**: `/home/user/downloadFromYoutube/DownloadFY/appsettings.json`
**Settings**:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

---

#### global.json
**Location**: `/home/user/downloadFromYoutube/global.json`
**Configuration**:
```json
{
  "sdk": {
    "version": "9.0.0",
    "rollForward": "latestMinor"
  }
}
```

---

### Database Connections and ORM

**No Database** - The application uses in-memory storage and file system:
- Task tracking: `Dictionary<Guid, TaskResult>` in memory
- Downloaded files: Stored in `{TaskId}/` directories on disk
- Temporary files: Created during video processing, deleted after merge

---

### Authentication/Authorization

**Current State**: No authentication/authorization implemented

**Swagger Configuration** (Program.cs lines 25-47):
- Bearer token scheme configured in Swagger UI
- Currently not enforced in actual endpoints
- All endpoints use `.AllowAnonymous()` except health check

**Future Enhancement**: API token authentication can be enabled using the configured Bearer scheme

---

## Code Patterns & Conventions

### Naming Conventions

**Classes**: PascalCase
- `MainService`
- `TaskResult`
- `QueueItem`

**Methods**: PascalCase
- `DoSmth()`
- `ProcessQueueAsync()`
- `DownloadVideos()`

**Private Fields**: camelCase with underscore prefix
- `_semaphore`
- `_queue`
- `_taskResults`

**Parameters/Variables**: camelCase
- `taskId`
- `videoUrls`
- `playlistUrl`

**Records**: PascalCase
- `DoSmthRequest`
- `DownloadVideoRequest`
- `DownloadSingleAudioRequest`

---

### Common Design Patterns

#### 1. Producer-Consumer Pattern
**Implementation**: Channel-based queue processing
- Producers: API endpoints adding items to queue
- Consumer: `ProcessQueueAsync()` background task

#### 2. Singleton Pattern
**Services**:
- `MainService` (registered as singleton)
- `YoutubeClient` (registered as singleton)

**Benefit**: Maintains queue state and rate limiting across requests

#### 3. Strategy Pattern
**Download Types**:
- Different processing strategies based on `DownloadType` enum
- `ProcessPlaylist()` for AudioPlaylist
- `ProcessVideos()` for Video
- `ProcessSingleAudio()` for SingleAudio

#### 4. Repository Pattern (Simplified)
**Implementation**: `MainService` acts as a repository for tasks
- `GetTaskStatus()` - Retrieve task
- `CleanupTask()` - Delete task
- `CompressDirectory()` - Prepare task for download

---

### Code Organization Principles

**Separation of Concerns**:
- **Program.cs** - API endpoints and configuration
- **MainService.cs** - Business logic and queue processing
- **index.html** - Frontend UI and client-side logic

**Dependency Injection**:
- Services registered in Program.cs
- Injected into endpoint handlers via parameters

**Async/Await Pattern**:
- All I/O operations use async/await
- Background processing with `Task.Run()` not needed (channel-based)

---

### Error Handling Approach

#### Backend Error Handling

**Try-Catch Blocks**:
- Surround all download operations
- Log errors using `ILogger<MainService>`
- Continue processing next item on error (fail-safe)

**Examples**:

1. **Playlist Processing** (MainService.cs lines 233-241):
```csharp
try
{
    manifest = await _youtubeClient.Videos.Streams.GetManifestAsync(video.Id);
}
catch (Exception ex)
{
    _logger.LogWarning(ex, "Failed to get manifest for video {VideoId}", video.Id);
    continue; // Skip to next video
}
```

2. **Queue Processing** (MainService.cs lines 181-196):
```csharp
catch (Exception ex)
{
    _logger.LogError(ex, "Error processing task {TaskId}", item.TaskId);
    lock (_taskResults)
    {
        taskResult.Status = TaskStatus.Failed;
        taskResult.Error = ex.Message;
    }
    item.CompletionSource.SetException(ex);
}
```

**Frontend Error Handling**:
- API errors caught in try-catch blocks
- User-friendly error messages displayed in status div
- Network errors logged to console

---

## Important Files & Locations

### Core Application Files

| File | Purpose | Location |
|------|---------|----------|
| `Program.cs` | API endpoints and configuration | `/home/user/downloadFromYoutube/DownloadFY/Program.cs` |
| `MainService.cs` | Core business logic | `/home/user/downloadFromYoutube/DownloadFY/MainService.cs` |
| `index.html` | Frontend UI | `/home/user/downloadFromYoutube/website/index.html` |

### Configuration Files

| File | Purpose | Location |
|------|---------|----------|
| `docker-compose.yml` | Multi-container orchestration | `/home/user/downloadFromYoutube/docker-compose.yml` |
| `Dockerfile` | Backend container definition | `/home/user/downloadFromYoutube/Dockerfile` |
| `Dockerfile.web` | Frontend nginx container | `/home/user/downloadFromYoutube/Dockerfile.web` |
| `nginx.conf` | Nginx reverse proxy config | `/home/user/downloadFromYoutube/nginx.conf` |
| `appsettings.json` | Production configuration | `/home/user/downloadFromYoutube/DownloadFY/appsettings.json` |
| `appsettings.Development.json` | Development configuration | `/home/user/downloadFromYoutube/DownloadFY/appsettings.Development.json` |
| `launchSettings.json` | Launch profiles | `/home/user/downloadFromYoutube/DownloadFY/Properties/launchSettings.json` |
| `global.json` | .NET SDK version | `/home/user/downloadFromYoutube/global.json` |

### Project Files

| File | Purpose | Location |
|------|---------|----------|
| `DownloadFY.csproj` | Project dependencies | `/home/user/downloadFromYoutube/DownloadFY/DownloadFY.csproj` |
| `DownloadFY.sln` | Visual Studio solution | `/home/user/downloadFromYoutube/DownloadFY.sln` |
| `.gitignore` | Git ignore rules | `/home/user/downloadFromYoutube/.gitignore` |
| `.env.example` | Environment variables template | `/home/user/downloadFromYoutube/.env.example` |

---

## Where to Find Specific Functionality

### Download Logic
- **Audio Playlist Downloads**: `MainService.cs` lines 205-293 (`ProcessPlaylist()`)
- **Video Downloads**: `MainService.cs` lines 295-451 (`ProcessVideos()`)
- **Single Audio Downloads**: `MainService.cs` lines 454-511 (`ProcessSingleAudio()`)
- **FFmpeg Merging**: `MainService.cs` lines 562-571 (`MergeStreamsAsync()`)

### Queue Management
- **Queue Processing**: `MainService.cs` lines 116-203 (`ProcessQueueAsync()`)
- **Task Queuing**: `MainService.cs` lines 29-114 (DoSmth, DownloadVideos, DownloadSingleAudio)
- **Queue Status**: `MainService.cs` line 637 (`GetQueueLength()`)

### File Operations
- **Filename Sanitization**: `MainService.cs` lines 573-635 (`SanitizeFileName()`)
- **ZIP Compression**: `MainService.cs` lines 647-678 (`CompressDirectory()`)
- **Single File Retrieval**: `MainService.cs` lines 705-735 (`GetSingleAudioFile()`)
- **Cleanup**: `MainService.cs` lines 673-703 (`CleanupTask()`)

### API Endpoints
- **All Endpoints**: `Program.cs` lines 58-248

### Frontend Logic
- **Download Type Selection**: `index.html` lines 234-255 (`updateDownloadType()`)
- **Form Submission**: `index.html` lines 267-339 (`submitUrls()`)
- **Status Polling**: `index.html` lines 349-381 (`checkDownloadStatus()`)
- **File Download**: `index.html` lines 383-424 (`downloadFile()`)

### Rate Limiting
- **Rate Limit Logic**: `MainService.cs` lines 275-282 (playlist), lines 431-438 (video)
- **Rate Limit Settings**: 25 downloads per 120 seconds

---

## Development Notes

### Build and Run Instructions

#### Using Docker Compose (Recommended)

1. **Build and start services**:
```bash
docker-compose up --build
```

2. **Access the application**:
- Frontend: http://localhost:1010
- Backend API: http://localhost:1000
- Swagger UI: http://localhost:1000/swagger

3. **Stop services**:
```bash
docker-compose down
```

#### Running Locally (Development)

**Prerequisites**:
- .NET 9.0 SDK
- FFmpeg installed and in PATH

**Backend**:
```bash
cd DownloadFY
dotnet restore
dotnet run
```

**Frontend** (requires web server):
```bash
# Using Python
cd website
python -m http.server 8080

# Or using npx
cd website
npx http-server -p 8080
```

---

### Project Structure

```
downloadFromYoutube/
├── DownloadFY/                    # Backend ASP.NET Core project
│   ├── Program.cs                 # API endpoints and configuration
│   ├── MainService.cs             # Core business logic
│   ├── DownloadFY.csproj          # Project dependencies
│   ├── appsettings.json           # Production config
│   ├── appsettings.Development.json  # Development config
│   └── Properties/
│       └── launchSettings.json    # Launch profiles
├── website/                       # Frontend static files
│   └── index.html                 # Single-page application
├── docker-compose.yml             # Multi-container orchestration
├── Dockerfile                     # Backend container
├── Dockerfile.web                 # Frontend nginx container
├── nginx.conf                     # Nginx configuration
├── DownloadFY.sln                 # Visual Studio solution
├── global.json                    # .NET SDK version
├── .env.example                   # Environment template
└── .gitignore                     # Git ignore rules
```

---

### Quirks and Special Considerations

#### 1. Rate Limiting
**Issue**: YouTube throttles downloads if too many requests are made
**Solution**: Built-in rate limiting (25 downloads per 120 seconds)
**Location**: MainService.cs lines 275-282, 431-438
**Note**: Shared counter across all download types

#### 2. Filename Sanitization
**Issue**: YouTube titles may contain invalid filesystem characters
**Solution**: Comprehensive sanitization function
**Location**: MainService.cs lines 573-635
**Handles**:
- Invalid characters (/, \, :, *, ?, ", <, >, |)
- Windows reserved names (CON, PRN, AUX, etc.)
- Unicode characters (Cyrillic, Chinese, etc.)
- Duplicate filenames (adds counter suffix)
- Maximum length (160 characters)

#### 3. Single-Threaded Processing
**Design**: Only one task processes at a time
**Reason**: Avoid overwhelming YouTube API and system resources
**Implementation**: Semaphore with maxCount=1
**Location**: MainService.cs line 14

#### 4. In-Memory Task Storage
**Limitation**: Task status lost on application restart
**Mitigation**: Frontend uses LocalStorage to resume polling
**Note**: Downloaded files persist in volume mount

#### 5. FFmpeg Dependency
**Requirement**: FFmpeg must be installed in container
**Usage**: Merging video and audio streams for video downloads
**Location**: Dockerfile lines installing ffmpeg
**Not Used For**: Audio-only downloads (direct stream download)

#### 6. No Duplicate File Handling Across Playlists
**Behavior**: Same video in multiple playlists downloads multiple times
**Location**: Each playlist has its own subdirectory
**Future Enhancement**: Add global deduplication based on video ID

#### 7. Frontend Polling Frequency
**Setting**: 1 second interval
**Location**: index.html line 346
**Note**: May cause excessive API calls for long-running tasks
**Future Enhancement**: Implement exponential backoff

#### 8. WebM Format for Audio
**Choice**: WebM format selected for audio streams
**Reason**: Highest bitrate typically available on YouTube
**Location**: MainService.cs lines 513-517 (`GetAudioStream()`)
**Alternative**: Could support opus or other formats

#### 9. Single Audio Download Type
**New Feature**: Downloads one audio file directly without ZIP
**Behavior**: Returns raw WebM file via separate endpoint
**Use Case**: Quick single song downloads without extraction
**Endpoints**:
  - POST `/download-single-audio` (queue)
  - GET `/data/{id}/download-single` (download)

---

### Areas That Need Attention or Refactoring

#### High Priority

1. **Authentication/Authorization**
   - Currently no access control
   - Swagger has Bearer config but not enforced
   - **Recommendation**: Implement API key authentication

2. **Error Recovery**
   - Failed tasks stay in failed state indefinitely
   - No retry mechanism
   - **Recommendation**: Add task retry logic with exponential backoff

3. **Resource Cleanup**
   - No automatic cleanup of old completed tasks
   - Disk space can fill up over time
   - **Recommendation**: Add scheduled cleanup job (e.g., delete tasks older than 24 hours)

4. **Logging**
   - Logs to console only
   - No structured logging or log aggregation
   - **Recommendation**: Add Serilog with file or external sink

#### Medium Priority

5. **Frontend Error Handling**
   - Network errors only logged to console
   - User sees generic "Processing..." during network issues
   - **Recommendation**: Add explicit network error messages

6. **Download Progress**
   - No progress indication during download
   - Users don't know how long to wait
   - **Recommendation**: Add progress tracking using `IProgress<T>`

7. **Concurrent Processing**
   - Currently limited to 1 task at a time
   - Could support multiple concurrent tasks with better resource management
   - **Recommendation**: Make semaphore count configurable

8. **Input Validation**
   - URL validation is basic string checking
   - Doesn't validate actual YouTube URL structure
   - **Recommendation**: Use YouTube URL parsing to validate format

#### Low Priority

9. **Configuration Management**
   - Hardcoded values (rate limits, timeouts, etc.)
   - **Recommendation**: Move to appsettings.json

10. **Testing**
    - No unit or integration tests
    - **Recommendation**: Add xUnit tests for critical paths

11. **Frontend Framework**
    - Vanilla JavaScript could benefit from a framework
    - **Consideration**: Evaluate React/Vue for better state management

12. **Database Persistence**
    - In-memory storage loses task history on restart
    - **Consideration**: Add SQLite for task persistence

---

## Quick Reference

### Common Tasks

| Task | Command/Location |
|------|------------------|
| Start application | `docker-compose up` |
| View logs | `docker-compose logs -f backend` |
| Access Swagger | http://localhost:1000/swagger |
| Access frontend | http://localhost:1010 |
| View downloaded files | Check volume `download-data` |
| Clean up old tasks | Use DELETE `/data/{id}` endpoint |

### Environment Variables

Currently none required. See `.env.example` for future configuration options.

### Ports

| Service | Host Port | Container Port |
|---------|-----------|----------------|
| Backend API | 1000 | 5000 |
| Frontend | 1010 | 80 |

### Volume Mounts

| Volume | Mount Point | Purpose |
|--------|-------------|---------|
| `download-data` | `/app` | Persistent storage for downloads |

---

## Troubleshooting

### Common Issues

**Issue**: "FFmpeg not found" error
**Solution**: Ensure ffmpeg is installed in the container (Dockerfile)

**Issue**: Downloads fail with "Rate limit exceeded"
**Solution**: Wait 120 seconds, rate limiter will reset automatically

**Issue**: Task stuck in "Processing" state
**Solution**: Check backend logs for errors, may need to restart service

**Issue**: Frontend shows "Task not found"
**Solution**: Task may have expired or been cleaned up, submit new request

**Issue**: Downloaded ZIP is empty
**Solution**: Check backend logs for download errors, videos may have been restricted or private

**Issue**: Single audio download returns ZIP instead of audio file
**Solution**: Ensure using `/download-single` endpoint, not `/download`

---

## Version History

**Current Version**: 1.0.0 (with Single Audio feature)

**Recent Changes**:
- Added single audio download functionality
- New endpoint: POST `/download-single-audio`
- New endpoint: GET `/data/{id}/download-single`
- Frontend radio button for "Single Audio" option
- Direct WebM file download without ZIP compression

---

## Future Enhancements

### Planned Features
1. User authentication and rate limiting per user
2. Download progress tracking and reporting
3. Support for additional platforms (Vimeo, SoundCloud, etc.)
4. Queue priority system
5. Scheduled downloads
6. Download history and favorites
7. Audio format conversion (MP3, FLAC, etc.)
8. Video quality selection
9. Subtitle download support
10. Playlist deduplication

### Technical Improvements
1. Database persistence (PostgreSQL or SQLite)
2. Distributed caching (Redis)
3. Background job processing (Hangfire)
4. Health checks and monitoring
5. Metrics and telemetry (Prometheus, Grafana)
6. Unit and integration tests
7. CI/CD pipeline
8. Load balancing for multiple instances

---

## Contact and Support

For issues, questions, or contributions:
- Review this documentation
- Check backend logs: `docker-compose logs backend`
- Check frontend console for JavaScript errors
- Verify YouTube URLs are valid and publicly accessible

---

**Document Version**: 1.0
**Last Updated**: 2025-01-10
**Maintained By**: Development Team
