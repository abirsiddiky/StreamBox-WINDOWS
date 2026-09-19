using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StreamBox.Models;

namespace StreamBox.Services;

/// <summary>
/// Proper Xtream Codes API client. Authenticates via player_api.php,
/// fetches live categories and streams, and builds standard /live/ URLs.
/// Falls back to get.php M3U when the Player API is unavailable.
/// </summary>
public sealed class XtreamClient
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient;

    public XtreamClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    // ── Public API ──

    /// <summary>
    /// Authenticates against the Xtream server using player_api.php.
    /// Returns auth=true if credentials are valid.
    /// </summary>
    public async Task<XtreamAuthResult> AuthenticateAsync(
        string server, string username, string password, CancellationToken ct = default)
    {
        var url = BuildApiUrl(server, username, password, null);
        Log.Info($"[Xtream] Authenticating server={server} username={username} password=***");

        var (body, finalUrl) = await FetchStringAsync(url, ct);
        if (body is null)
            return new XtreamAuthResult(false, "EMPTY_RESPONSE", "Server returned an empty response");

        if (IsHtml(body))
            return new XtreamAuthResult(false, "HTML_RESPONSE", "Server returned an HTML page instead of API data");

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("user_info", out var userInfo))
            {
                var auth = userInfo.TryGetProperty("auth", out var authProp) ? authProp.GetInt32() : 0;
                var status = userInfo.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : "Unknown";
                Log.Info($"[Xtream] Authentication result: auth={auth} status={status}");

                if (auth == 1)
                    return new XtreamAuthResult(true, null, null);

                return new XtreamAuthResult(false, "AUTH_FAILED", $"Authentication failed (status={status})");
            }

            // Some servers return {"auth":1,...} at the root level
            if (root.TryGetProperty("auth", out var rootAuth) && rootAuth.GetInt32() == 1)
            {
                Log.Info("[Xtream] Authentication result: auth=1 (root-level)");
                return new XtreamAuthResult(true, null, null);
            }

            Log.Warn("[Xtream] Unexpected JSON structure — no user_info or auth field");
            return new XtreamAuthResult(false, "INVALID_JSON", "Unexpected server response");
        }
        catch (JsonException ex)
        {
            Log.Warn($"[Xtream] Invalid JSON from auth endpoint: {ex.Message}");
            return new XtreamAuthResult(false, "INVALID_JSON", "Server returned invalid JSON");
        }
    }

    /// <summary>
    /// Fetches live categories from the Xtream Player API.
    /// </summary>
    public async Task<List<XtreamCategory>> GetLiveCategoriesAsync(
        string server, string username, string password, CancellationToken ct = default)
    {
        var url = BuildApiUrl(server, username, password, "get_live_categories");
        Log.Info("[Xtream] Loading live categories");

        var (body, _) = await FetchStringAsync(url, ct);
        if (body is null || IsHtml(body))
        {
            Log.Warn("[Xtream] Live categories: empty or HTML response");
            return new List<XtreamCategory>();
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var categories = new List<XtreamCategory>();

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var cat = new XtreamCategory
                    {
                        Id = item.TryGetProperty("category_id", out var id) ? GetStringOrNumber(id) ?? "" : "",
                        Name = item.TryGetProperty("category_name", out var name) ? GetStringOrNumber(name) ?? "" : "",
                        ParentId = item.TryGetProperty("parent_id", out var pid) ? GetStringOrNumber(pid) : null
                    };
                    if (!string.IsNullOrWhiteSpace(cat.Name))
                        categories.Add(cat);
                }
            }

            Log.Info($"[Xtream] Live categories received: {categories.Count}");
            return categories;
        }
        catch (JsonException ex)
        {
            Log.Warn($"[Xtream] Invalid JSON from get_live_categories: {ex.Message}");
            return new List<XtreamCategory>();
        }
    }

    /// <summary>
    /// Fetches live streams from the Xtream Player API.
    /// </summary>
    public async Task<List<XtreamStream>> GetLiveStreamsAsync(
        string server, string username, string password, CancellationToken ct = default)
    {
        var url = BuildApiUrl(server, username, password, "get_live_streams");
        Log.Info("[Xtream] Loading live streams");

        var (body, _) = await FetchStringAsync(url, ct);
        if (body is null || IsHtml(body))
        {
            Log.Warn("[Xtream] Live streams: empty or HTML response");
            return new List<XtreamStream>();
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var streams = new List<XtreamStream>();

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var stream = new XtreamStream
                    {
                        StreamId = item.TryGetProperty("stream_id", out var sid) ? GetInt64Safe(sid) : 0,
                        Name = item.TryGetProperty("name", out var name) ? GetStringOrNumber(name) ?? "" : "",
                        StreamIcon = item.TryGetProperty("stream_icon", out var icon) ? GetStringOrNumber(icon) : null,
                        EpgChannelId = item.TryGetProperty("epg_channel_id", out var epg) ? GetStringOrNumber(epg) : null,
                        CategoryId = item.TryGetProperty("category_id", out var cid) ? GetStringOrNumber(cid) ?? "" : "",
                        CategoryName = item.TryGetProperty("category_name", out var cname) ? GetStringOrNumber(cname) : null,
                        ContainerExtension = item.TryGetProperty("container_extension", out var ext) ? GetStringOrNumber(ext) : null,
                        TvArchive = item.TryGetProperty("tv_archive", out var arch) ? GetInt32Safe(arch) : 0,
                        TvArchiveDuration = item.TryGetProperty("tv_archive_duration", out var archDur) ? GetInt32Safe(archDur) : 0,
                        CustomSid = item.TryGetProperty("custom_sid", out var sid2) ? GetStringOrNumber(sid2) : null,
                        Added = item.TryGetProperty("added", out var added) ? GetStringOrNumber(added) : null
                    };

                    if (stream.StreamId > 0 && !string.IsNullOrWhiteSpace(stream.Name))
                        streams.Add(stream);
                }
            }

            Log.Info($"[Xtream] Live streams received: {streams.Count}");
            return streams;
        }
        catch (JsonException ex)
        {
            Log.Warn($"[Xtream] Invalid JSON from get_live_streams: {ex.Message}");
            return new List<XtreamStream>();
        }
    }

    /// <summary>
    /// Builds a standard Xtream live stream URL.
    /// </summary>
    public static string BuildLiveStreamUrl(string server, string username, string password, long streamId, string? extension)
    {
        var ext = string.IsNullOrWhiteSpace(extension) ? "ts" : extension;
        var baseUri = server.TrimEnd('/');
        return $"{baseUri}/live/{Uri.EscapeDataString(username)}/{Uri.EscapeDataString(password)}/{streamId}.{ext}";
    }

    /// <summary>
    /// Attempts to fetch the M3U playlist via get.php as a fallback.
    /// Validates the response is actually M3U content before returning it.
    /// Returns null if the response is empty, HTML, or not valid M3U.
    /// </summary>
    public async Task<string?> TryGetM3uFallbackAsync(
        string server, string username, string password, CancellationToken ct = default)
    {
        var baseUri = server.TrimEnd('/');
        var url = $"{baseUri}/get.php?username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}&type=m3u_plus&output=ts";
        Log.Info("[Xtream] Attempting get.php M3U fallback");

        var (body, _) = await FetchStringAsync(url, ct);
        if (body is null)
        {
            Log.Warn("[Xtream] get.php returned empty response");
            return null;
        }

        if (IsHtml(body))
        {
            Log.Warn("[Xtream] get.php returned HTML instead of M3U");
            return null;
        }

        if (!IsValidM3u(body))
        {
            Log.Warn($"[Xtream] get.php response is not valid M3U (starts with: '{(body.Length > 40 ? body[..40] : body)}')");
            return null;
        }

        Log.Info($"[Xtream] get.php M3U fallback succeeded: {body.Length} chars");
        return body;
    }

    /// <summary>
    /// Full Xtream refresh: authenticate → categories → streams → Channel list.
    /// Returns null if authentication fails (caller should show error).
    /// Returns empty list only if the server genuinely has zero live channels.
    /// </summary>
    public async Task<XtreamRefreshResult> RefreshAsync(
        string server, string username, string password, long playlistId, CancellationToken ct = default)
    {
        // Step 1: Authenticate
        var auth = await AuthenticateAsync(server, username, password, ct);
        if (!auth.Success)
            return new XtreamRefreshResult(null, auth.ErrorCode ?? "AUTH_FAILED", auth.ErrorMessage ?? "Authentication failed");

        // Step 2: Get categories (for group mapping)
        var categories = await GetLiveCategoriesAsync(server, username, password, ct);
        var catMap = new Dictionary<string, string>();
        foreach (var cat in categories)
        {
            if (!string.IsNullOrWhiteSpace(cat.Id) && !string.IsNullOrWhiteSpace(cat.Name))
                catMap[cat.Id] = cat.Name;
        }

        // Step 3: Get live streams
        var streams = await GetLiveStreamsAsync(server, username, password, ct);

        // If Player API returned 0 streams, try get.php fallback
        if (streams.Count == 0)
        {
            Log.Info("[Xtream] Player API returned 0 streams, trying get.php fallback");
            var m3u = await TryGetM3uFallbackAsync(server, username, password, ct);
            if (m3u is not null)
            {
                // Parse M3U and return as channels
                var m3uChannels = PlaylistService.ParseM3u(m3u);
                foreach (var ch in m3uChannels)
                    ch.PlaylistId = playlistId;
                Log.Info($"[Xtream] get.php fallback produced {m3uChannels.Count} channels");
                return new XtreamRefreshResult(m3uChannels, null, null);
            }

            return new XtreamRefreshResult(new List<Channel>(), "NO_LIVE_CHANNELS", "Server reported zero live channels");
        }

        // Step 4: Map streams to Channel objects
        var channels = new List<Channel>(streams.Count);
        for (var i = 0; i < streams.Count; i++)
        {
            var s = streams[i];
            var groupTitle = !string.IsNullOrWhiteSpace(s.CategoryName)
                ? s.CategoryName
                : catMap.TryGetValue(s.CategoryId, out var catName) ? catName : "Xtream";

            var streamUrl = BuildLiveStreamUrl(server, username, password, s.StreamId, s.ContainerExtension);

            channels.Add(new Channel
            {
                Name = s.Name,
                GroupTitle = groupTitle,
                LogoUrl = string.IsNullOrWhiteSpace(s.StreamIcon) ? null : s.StreamIcon,
                StreamUrl = streamUrl,
                SortOrder = i,
                PlaylistId = playlistId
            });
        }

        Log.Info($"[Xtream] Mapped StreamBox channels: {channels.Count}");
        return new XtreamRefreshResult(channels, null, null);
    }

    // ── URL Building ──

    private static string BuildApiUrl(string server, string username, string password, string? action)
    {
        var baseUri = server.TrimEnd('/');
        var qs = $"username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}";
        if (!string.IsNullOrEmpty(action))
            qs += $"&action={action}";
        return $"{baseUri}/player_api.php?{qs}";
    }

    // ── HTTP Helpers ──

    private async Task<(string? Body, Uri? FinalUrl)> FetchStringAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url, ct);
            var finalUrl = response.RequestMessage?.RequestUri;
            var statusCode = (int)response.StatusCode;
            var contentType = response.Content.Headers.ContentType?.MediaType;
            var contentLength = response.Content.Headers.ContentLength;

            Log.Info($"[Xtream] HTTP {statusCode} Content-Type={contentType} Content-Length={contentLength} FinalUrl={finalUrl}");

            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"[Xtream] HTTP error {statusCode} for {finalUrl}");
                return (null, finalUrl);
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            return (body, finalUrl);
        }
        catch (TaskCanceledException)
        {
            Log.Warn("[Xtream] Request timed out");
            return (null, null);
        }
        catch (HttpRequestException ex)
        {
            Log.Warn($"[Xtream] HTTP request failed: {ex.Message}");
            return (null, null);
        }
    }

    // ── JSON Helpers ──

    /// <summary>
    /// Reads a JsonElement as a string, tolerating both string and number types.
    /// Many Xtream servers return IDs as numbers, not strings.
    /// </summary>
    private static string? GetStringOrNumber(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.ToString(),
            _ => null
        };
    }

    private static int GetInt32Safe(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.GetInt32(),
            JsonValueKind.String => int.TryParse(el.GetString(), out var v) ? v : 0,
            _ => 0
        };
    }

    private static long GetInt64Safe(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.GetInt64(),
            JsonValueKind.String => long.TryParse(el.GetString(), out var v) ? v : 0,
            _ => 0
        };
    }

    // ── Content Validation ──

    private static bool IsHtml(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<head", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<body", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidM3u(string text)
    {
        // Strip BOM and leading whitespace
        var trimmed = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return trimmed.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase);
    }
}

// ── DTOs ──

public sealed class XtreamAuthResult
{
    public bool Success { get; }
    public string? ErrorCode { get; }
    public string? ErrorMessage { get; }

    public XtreamAuthResult(bool success, string? errorCode, string? errorMessage)
    {
        Success = success;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }
}

public sealed class XtreamRefreshResult
{
    public List<Channel>? Channels { get; }
    public string? ErrorCode { get; }
    public string? ErrorMessage { get; }

    public XtreamRefreshResult(List<Channel>? channels, string? errorCode, string? errorMessage)
    {
        Channels = channels;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }
}

public sealed class XtreamCategory
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ParentId { get; set; }
}

public sealed class XtreamStream
{
    public long StreamId { get; set; }
    public string Name { get; set; } = "";
    public string? StreamIcon { get; set; }
    public string? EpgChannelId { get; set; }
    public string CategoryId { get; set; } = "";
    public string? CategoryName { get; set; }
    public string? ContainerExtension { get; set; }
    public int TvArchive { get; set; }
    public int TvArchiveDuration { get; set; }
    public string? CustomSid { get; set; }
    public string? Added { get; set; }
}
