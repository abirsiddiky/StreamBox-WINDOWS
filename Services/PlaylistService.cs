using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using StreamBox.Models;

namespace StreamBox.Services;

public sealed class PlaylistService
{
    private const string DefaultPlaylistUrl = "https://raw.githubusercontent.com/ahan443/FAST-IPTV/refs/heads/main/z.m3u";
    private const string SourceKindKey = "playlist_source_kind";
    private const string SourceValueKey = "playlist_source_value";

    private static readonly Regex AttributeRegex = new(@"(?<key>[\w-]+)=""(?<value>[^""]*)""", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly DatabaseService _databaseService;

    public PlaylistService(HttpClient httpClient, DatabaseService databaseService)
    {
        _httpClient = httpClient;
        _databaseService = databaseService;
    }

    public Task<IReadOnlyList<Channel>> LoadCachedChannelsAsync(CancellationToken cancellationToken = default)
        => _databaseService.LoadChannelsAsync(cancellationToken);

    public async Task<IReadOnlyList<Channel>> RefreshChannelsAsync(CancellationToken cancellationToken = default)
    {
        var source = await GetPlaylistSourceAsync(cancellationToken);
        var m3uText = await ReadPlaylistTextAsync(source, cancellationToken);
        var channels = ParseM3u(m3uText);

        await _databaseService.SaveChannelsAsync(channels, cancellationToken);
        Log.Info($"Playlist refresh completed with {channels.Count} channels");
        return channels;
    }

    public async Task<string> GetPlaylistSourceDisplayTextAsync(CancellationToken cancellationToken = default)
    {
        var source = await GetPlaylistSourceAsync(cancellationToken);
        return source.Kind switch
        {
            PlaylistSourceKind.Default => "Default playlist",
            PlaylistSourceKind.CustomUrl => "Custom URL",
            PlaylistSourceKind.CustomFile => $"File: {Path.GetFileName(source.Value ?? string.Empty)}",
            _ => "Default playlist"
        };
    }

    public async Task SetCustomUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        await _databaseService.SetSettingAsync(SourceKindKey, PlaylistSourceKind.CustomUrl.ToString(), cancellationToken);
        await _databaseService.SetSettingAsync(SourceValueKey, url, cancellationToken);
        Log.Info("Playlist source set to custom URL");
    }

    public async Task SetCustomFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await _databaseService.SetSettingAsync(SourceKindKey, PlaylistSourceKind.CustomFile.ToString(), cancellationToken);
        await _databaseService.SetSettingAsync(SourceValueKey, filePath, cancellationToken);
        Log.Info("Playlist source set to custom file");
    }

    public async Task ResetToDefaultAsync(CancellationToken cancellationToken = default)
    {
        await _databaseService.SetSettingAsync(SourceKindKey, PlaylistSourceKind.Default.ToString(), cancellationToken);
        await _databaseService.DeleteSettingAsync(SourceValueKey, cancellationToken);
        Log.Info("Playlist source reset to default");
    }

    private async Task<PlaylistSource> GetPlaylistSourceAsync(CancellationToken cancellationToken)
    {
        var kindRaw = await _databaseService.GetSettingAsync(SourceKindKey, cancellationToken);
        var value = await _databaseService.GetSettingAsync(SourceValueKey, cancellationToken);

        if (!Enum.TryParse<PlaylistSourceKind>(kindRaw, ignoreCase: true, out var kind))
        {
            kind = PlaylistSourceKind.Default;
        }

        return kind switch
        {
            PlaylistSourceKind.CustomUrl when !string.IsNullOrWhiteSpace(value) => new PlaylistSource(kind, value),
            PlaylistSourceKind.CustomFile when !string.IsNullOrWhiteSpace(value) => new PlaylistSource(kind, value),
            _ => new PlaylistSource(PlaylistSourceKind.Default, DefaultPlaylistUrl)
        };
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

    private static List<Channel> ParseM3u(string text)
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

        // Sanitize logo URL: strip backticks, HTML entities, surrounding quotes/spaces
        // M3U playlists often have formats like: tvg-logo="`https://example.com/logo.png`"
        if (logoUrl is not null)
        {
            logoUrl = logoUrl
                .Trim('`', '"', '\'', ' ')
                .Replace("&amp;", "&")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&#39;", "'")
                .Replace("&quot;", "\"");

            // Validate it looks like a URL (has scheme)
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
}
