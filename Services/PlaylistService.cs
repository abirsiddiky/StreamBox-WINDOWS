using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using StreamBox.Models;

namespace StreamBox.Services;

public sealed class PlaylistService
{
    private const string DefaultPlaylistUrl = "https://raw.githubusercontent.com/ahan443/FAST-IPTV/refs/heads/main/z.m3u";
    private const string ActivePlaylistIdKey = "active_playlist_id";

    private static readonly Regex AttributeRegex = new(@"(?<key>[\w-]+)=""(?<value>[^""]*)""", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient;
    private readonly DatabaseService _databaseService;
    private readonly StalkerService _stalkerService;
    private readonly XtreamClient _xtreamClient;

    public PlaylistService(HttpClient httpClient, DatabaseService databaseService, StalkerService stalkerService, XtreamClient xtreamClient)
    {
        _httpClient = httpClient;
        _databaseService = databaseService;
        _stalkerService = stalkerService;
        _xtreamClient = xtreamClient;
    }

    // ── Playlist CRUD ──

    public Task<IReadOnlyList<Playlist>> LoadPlaylistsAsync(CancellationToken cancellationToken = default)
        => _databaseService.LoadPlaylistsAsync(cancellationToken);

    public async Task<long> AddPlaylistAsync(string name, string sourceKind, string? sourceValue, CancellationToken cancellationToken = default)
    {
        var existing = await _databaseService.LoadPlaylistsAsync(cancellationToken);
        var playlist = new Playlist
        {
            Name = name,
            SourceKind = sourceKind,
            SourceValue = sourceValue,
            IsEnabled = true,
            SortOrder = existing.Count
        };
        var id = await _databaseService.InsertPlaylistAsync(playlist, cancellationToken);
        playlist.Id = id;
        Log.Info($"Playlist added: '{name}' (id={id}, kind={sourceKind})");
        return id;
    }

    public async Task RemovePlaylistAsync(long playlistId, CancellationToken cancellationToken = default)
    {
        await _databaseService.DeletePlaylistAsync(playlistId, cancellationToken);
        Log.Info($"Playlist deleted (id={playlistId})");
    }

    public async Task RenamePlaylistAsync(long playlistId, string newName, CancellationToken cancellationToken = default)
    {
        var playlists = await _databaseService.LoadPlaylistsAsync(cancellationToken);
        var playlist = playlists.FirstOrDefault(p => p.Id == playlistId);
        if (playlist is null) return;
        playlist.Name = newName;
        await _databaseService.UpdatePlaylistAsync(playlist, cancellationToken);
        Log.Info($"Playlist renamed: id={playlistId} -> '{newName}'");
    }

    // ── Active playlist tracking ──

    public async Task<long?> GetActivePlaylistIdAsync(CancellationToken cancellationToken = default)
    {
        var raw = await _databaseService.GetSettingAsync(ActivePlaylistIdKey, cancellationToken);
        if (long.TryParse(raw, out var id)) return id;
        return null;
    }

    public async Task SetActivePlaylistIdAsync(long playlistId, CancellationToken cancellationToken = default)
    {
        await _databaseService.SetSettingAsync(ActivePlaylistIdKey, playlistId.ToString(), cancellationToken);
    }

    // ── Channel operations (playlist-scoped) ──

    public Task<IReadOnlyList<Channel>> LoadCachedChannelsAsync(long playlistId, CancellationToken cancellationToken = default)
        => _databaseService.LoadPlaylistChannelsAsync(playlistId, cancellationToken);

    public async Task<IReadOnlyList<Channel>> RefreshPlaylistChannelsAsync(long playlistId, CancellationToken cancellationToken = default)
    {
        var playlists = await _databaseService.LoadPlaylistsAsync(cancellationToken);
        var playlist = playlists.FirstOrDefault(p => p.Id == playlistId);
        if (playlist is null)
        {
            Log.Warn($"RefreshPlaylistChannelsAsync: playlist id={playlistId} not found");
            return Array.Empty<Channel>();
        }

        // Stalker playlists use a completely different protocol — handle separately.
        if (playlist.SourceKind == "Stalker")
        {
            return await RefreshStalkerPlaylistAsync(playlistId, playlist, cancellationToken);
        }

        // Xtream playlists use the Player API — handle separately.
        if (playlist.SourceKind == "Xtream")
        {
            return await RefreshXtreamPlaylistAsync(playlistId, playlist, cancellationToken);
        }

        var source = ResolveSource(playlist);
        string m3uText;
        try
        {
            m3uText = await ReadPlaylistTextAsync(source, cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to fetch playlist '{playlist.Name}': {ex.Message}");
            throw;
        }

        var channels = ParseM3u(m3uText);
        foreach (var ch in channels)
        {
            ch.PlaylistId = playlistId;
        }

        await _databaseService.SavePlaylistChannelsAsync(playlistId, channels, cancellationToken);
        Log.Info($"Playlist '{playlist.Name}' refreshed: {channels.Count} channels");
        return channels;
    }

    private async Task<IReadOnlyList<Channel>> RefreshStalkerPlaylistAsync(
        long playlistId, Playlist playlist, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlist.SourceValue))
        {
            Log.Warn($"Stalker playlist '{playlist.Name}' has no source value");
            return Array.Empty<Channel>();
        }

        StalkerCreds creds;
        try
        {
            creds = JsonSerializer.Deserialize<StalkerCreds>(playlist.SourceValue, JsonOpts) ?? throw new JsonException("null");
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to parse Stalker credentials for '{playlist.Name}': {ex.Message}");
            return Array.Empty<Channel>();
        }

        var portal = creds.Portal?.TrimEnd('/') ?? "";
        var mac = creds.Mac ?? "";
        if (string.IsNullOrWhiteSpace(portal) || string.IsNullOrWhiteSpace(mac))
        {
            Log.Warn($"Stalker playlist '{playlist.Name}' has missing portal or mac");
            return Array.Empty<Channel>();
        }

        try
        {
            var token = await _stalkerService.HandshakeAsync(portal, mac, cancellationToken);

            // Fetch genres for GroupTitle mapping
            var genreMap = new Dictionary<string, string>();
            try
            {
                var genres = await _stalkerService.GetGenresAsync(portal, mac, token, cancellationToken);
                foreach (var (id, title) in genres)
                    genreMap[id] = title;
            }
            catch (Exception ex)
            {
                Log.Warn($"Stalker GetGenres failed (non-fatal): {ex.Message}");
            }

            var stalkerChannels = await _stalkerService.GetChannelsAsync(portal, mac, token, cancellationToken);

            var channels = new List<Channel>();
            for (var i = 0; i < stalkerChannels.Count; i++)
            {
                var sc = stalkerChannels[i];
                channels.Add(new Channel
                {
                    Name = sc.Name,
                    GroupTitle = genreMap.TryGetValue(sc.GenreId, out var genreTitle) ? genreTitle : "Stalker",
                    LogoUrl = sc.Logo,
                    StreamUrl = "", // resolved at play time, not stored
                    StalkerCmd = sc.Cmd,
                    StalkerPortalUrl = portal,
                    StalkerMac = mac,
                    SortOrder = i,
                    PlaylistId = playlistId
                });
            }

            await _databaseService.SavePlaylistChannelsAsync(playlistId, channels, cancellationToken);
            Log.Info($"Stalker playlist '{playlist.Name}' refreshed: {channels.Count} channels");
            return channels;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to fetch Stalker playlist '{playlist.Name}': {ex.Message}");
            throw;
        }
    }

    private async Task<IReadOnlyList<Channel>> RefreshXtreamPlaylistAsync(
        long playlistId, Playlist playlist, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playlist.SourceValue))
        {
            Log.Warn($"[Xtream] Playlist '{playlist.Name}' has no source value");
            return await LoadCachedOrEmpty(playlistId, cancellationToken);
        }

        XtreamCreds creds;
        try
        {
            creds = JsonSerializer.Deserialize<XtreamCreds>(playlist.SourceValue, JsonOpts)
                     ?? throw new JsonException("null");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Xtream] Failed to parse credentials for '{playlist.Name}': {ex.Message}");
            return await LoadCachedOrEmpty(playlistId, cancellationToken);
        }

        var server = creds.Server?.Trim().Trim('`', '\'', '"') ?? "";
        var username = creds.Username ?? "";
        var password = creds.Password ?? "";

        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            Log.Warn($"[Xtream] Playlist '{playlist.Name}' has incomplete credentials");
            return await LoadCachedOrEmpty(playlistId, cancellationToken);
        }

        try
        {
            var result = await _xtreamClient.RefreshAsync(server, username, password, playlistId, cancellationToken);

            if (result.Channels is null)
            {
                // Authentication or API failure — keep cached channels
                Log.Warn($"[Xtream] Refresh failed for '{playlist.Name}': [{result.ErrorCode}] {result.ErrorMessage}");
                return await LoadCachedOrEmpty(playlistId, cancellationToken);
            }

            if (result.Channels.Count == 0)
            {
                // Server genuinely has zero channels — don't overwrite cached
                Log.Warn($"[Xtream] Server returned 0 channels for '{playlist.Name}', keeping cached channels");
                return await LoadCachedOrEmpty(playlistId, cancellationToken);
            }

            // Success — save and return
            await _databaseService.SavePlaylistChannelsAsync(playlistId, result.Channels, cancellationToken);
            Log.Info($"[Xtream] Playlist '{playlist.Name}' refreshed: {result.Channels.Count} channels");
            return result.Channels;
        }
        catch (Exception ex)
        {
            Log.Error($"[Xtream] Unexpected error refreshing '{playlist.Name}': {ex.Message}");
            return await LoadCachedOrEmpty(playlistId, cancellationToken);
        }
    }

    /// <summary>
    /// Returns cached channels if any exist, otherwise empty list.
    /// Used to avoid overwriting valid cached data on refresh failure.
    /// </summary>
    private async Task<IReadOnlyList<Channel>> LoadCachedOrEmpty(long playlistId, CancellationToken ct)
    {
        try
        {
            var cached = await _databaseService.LoadPlaylistChannelsAsync(playlistId, ct);
            if (cached.Count > 0)
            {
                Log.Info($"[Xtream] Using {cached.Count} cached channels");
                return cached;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[Xtream] Failed to load cached channels: {ex.Message}");
        }
        return Array.Empty<Channel>();
    }

    // ── M3U Export ──

    public async Task<string> ExportPlaylistToM3uAsync(long playlistId, CancellationToken cancellationToken = default)
    {
        var channels = await _databaseService.LoadPlaylistChannelsAsync(playlistId, cancellationToken);
        var playlists = await _databaseService.LoadPlaylistsAsync(cancellationToken);
        var playlist = playlists.FirstOrDefault(p => p.Id == playlistId);
        var playlistName = playlist?.Name ?? "playlist";

        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine($"# Playlists exported from StreamBox");
        sb.AppendLine($"# Playlist: {playlistName}");
        sb.AppendLine($"# Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        foreach (var ch in channels)
        {
            // Build #EXTINF line with attributes
            var attrs = new List<string>();
            if (!string.IsNullOrWhiteSpace(ch.GroupTitle) && ch.GroupTitle != "Ungrouped")
            {
                attrs.Add($"group-title=\"{ch.GroupTitle}\"");
            }
            if (!string.IsNullOrWhiteSpace(ch.LogoUrl))
            {
                attrs.Add($"tvg-logo=\"{ch.LogoUrl}\"");
            }

            var attrStr = attrs.Count > 0 ? " " + string.Join(" ", attrs) : "";
            sb.AppendLine($"#EXTINF:-1{attrStr},{ch.Name}");

            // Extra headers as EXTVLCOPT
            if (!string.IsNullOrWhiteSpace(ch.UserAgent))
            {
                sb.AppendLine($"#EXTVLCOPT:http-user-agent={ch.UserAgent}");
            }
            if (ch.ExtraHeaders is { Count: > 0 })
            {
                foreach (var kv in ch.ExtraHeaders)
                {
                    // Skip http-user-agent since we handle it above
                    if (!kv.Key.Equals("http-user-agent", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"#EXTVLCOPT:{kv.Key}={kv.Value}");
                    }
                }
            }

            sb.AppendLine(ch.StreamUrl);
        }

        return sb.ToString();
    }

    public async Task ExportPlaylistToFileAsync(long playlistId, string filePath, CancellationToken cancellationToken = default)
    {
        var m3uContent = await ExportPlaylistToM3uAsync(playlistId, cancellationToken);
        await File.WriteAllTextAsync(filePath, m3uContent, Encoding.UTF8, cancellationToken);
        Log.Info($"Playlist exported to: {filePath}");
    }

    // ── Source resolution ──

    private static PlaylistSource ResolveSource(Playlist playlist)
    {
        var kind = playlist.SourceKind switch
        {
            "CustomUrl" => PlaylistSourceKind.CustomUrl,
            "CustomFile" => PlaylistSourceKind.CustomFile,
            _ => PlaylistSourceKind.Default
        };

        if (kind == PlaylistSourceKind.Default || string.IsNullOrWhiteSpace(playlist.SourceValue))
        {
            return new PlaylistSource(PlaylistSourceKind.Default, DefaultPlaylistUrl);
        }

        return new PlaylistSource(kind, playlist.SourceValue);
    }

    private async Task<string> ReadPlaylistTextAsync(PlaylistSource source, CancellationToken cancellationToken)
    {
        switch (source.Kind)
        {
            case PlaylistSourceKind.CustomFile:
                Log.Info("Loading playlist from custom file");
                return await File.ReadAllTextAsync(source.Value!, cancellationToken);

            case PlaylistSourceKind.CustomUrl:
                Log.Info("Loading playlist from custom URL");
                return await _httpClient.GetStringAsync(source.Value!, cancellationToken);

            default:
                Log.Info("Loading playlist from default source");
                return await _httpClient.GetStringAsync(source.Value!, cancellationToken);
        }
    }

    // ── M3U Parser ──

    internal static List<Channel> ParseM3u(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var channels = new List<Channel>();
        var sortOrder = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var channel = ParseExtInf(line);

            for (i = i + 1; i < lines.Length; i++)
            {
                var blockLine = lines[i].Trim();

                if (string.IsNullOrWhiteSpace(blockLine))
                {
                    continue;
                }

                if (blockLine.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
                {
                    i--;
                    break;
                }

                if (blockLine.StartsWith("#EXTVLCOPT:", StringComparison.OrdinalIgnoreCase))
                {
                    ParseExtVlcOpt(blockLine, channel);
                    continue;
                }

                if (blockLine.StartsWith("#EXTHTTP:", StringComparison.OrdinalIgnoreCase))
                {
                    ParseExtHttp(blockLine, channel);
                    continue;
                }

                if (!blockLine.StartsWith('#'))
                {
                    channel.StreamUrl = blockLine;
                    channel.SortOrder = sortOrder++;
                    channels.Add(channel);
                    break;
                }
            }
        }

        return channels;
    }

    private static Channel ParseExtInf(string line)
    {
        var commaIndex = line.LastIndexOf(',');
        var name = commaIndex >= 0 ? line[(commaIndex + 1)..].Trim() : "Unnamed Channel";
        var metadata = commaIndex >= 0 ? line[..commaIndex] : line;
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttributeRegex.Matches(metadata))
        {
            attributes[m.Groups["key"].Value] = m.Groups["value"].Value;
        }

        attributes.TryGetValue("group-title", out var groupTitle);
        attributes.TryGetValue("tvg-logo", out var logoUrl);

        if (logoUrl is not null)
        {
            logoUrl = logoUrl
                .Trim('`', '"', '\'', ' ')
                .Replace("&amp;", "&")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&#39;", "'")
                .Replace("&quot;", "\"");

            if (!Uri.TryCreate(logoUrl, UriKind.Absolute, out _))
            {
                Log.Warn($"Invalid logo URL for channel '{name}': {(logoUrl.Length > 60 ? logoUrl[..60] + "..." : logoUrl)}");
                logoUrl = null;
            }
        }

        return new Channel
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Unnamed Channel" : name,
            GroupTitle = string.IsNullOrWhiteSpace(groupTitle) ? "Ungrouped" : groupTitle,
            LogoUrl = string.IsNullOrWhiteSpace(logoUrl) ? null : logoUrl
        };
    }

    private static void ParseExtVlcOpt(string line, Channel channel)
    {
        var payload = line["#EXTVLCOPT:".Length..];
        var separatorIndex = payload.IndexOf('=');
        if (separatorIndex <= 0)
        {
            return;
        }

        var key = payload[..separatorIndex].Trim();
        var value = payload[(separatorIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (key.Equals("http-user-agent", StringComparison.OrdinalIgnoreCase))
        {
            channel.UserAgent = value;
            return;
        }

        channel.ExtraHeaders ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        channel.ExtraHeaders[key] = value;
    }

    private static void ParseExtHttp(string line, Channel channel)
    {
        var payload = line["#EXTHTTP:".Length..].Trim();
        if (string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(payload);
            if (parsed is null || parsed.Count == 0)
            {
                return;
            }

            channel.ExtraHeaders ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in parsed)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                {
                    channel.ExtraHeaders[pair.Key] = pair.Value;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Skipping invalid #EXTHTTP block: {ex.Message}");
        }
    }

    private enum PlaylistSourceKind
    {
        Default,
        CustomUrl,
        CustomFile
    }

    private sealed record PlaylistSource(PlaylistSourceKind Kind, string? Value);

    private sealed record XtreamCreds
    {
        public string? Server { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
    }

    private sealed record StalkerCreds
    {
        public string? Portal { get; set; }
        public string? Mac { get; set; }
    }
}
