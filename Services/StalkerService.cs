using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StreamBox.Services;

public sealed class StalkerService
{
    private const string UserAgent =
        "Mozilla/5.0 (QtEmbedded; U; Linux; C) AppleWebKit/533.3 (KHTML, like Gecko) MAG200 stbapp ver: 2 rev: 250 Safari/533.3";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;

    public StalkerService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Performs the Stalker handshake + get_profile sequence and returns the session token.
    /// </summary>
    public async Task<string> HandshakeAsync(string portalUrl, string mac, CancellationToken ct = default)
    {
        portalUrl = portalUrl.TrimEnd('/');

        // Step 1: Handshake
        var handshakeUrl = $"{portalUrl}/portal.php?type=stb&action=handshake&token=&JsHttpRequest=1-xml";
        var token = await GetRequestAsync(portalUrl, mac, null, handshakeUrl, ct,
            root => root.GetProperty("js").GetProperty("token").GetString() ?? "");

        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Stalker handshake returned empty token");

        // Step 2: get_profile (best-effort, some portals require it)
        try
        {
            var profileUrl = $"{portalUrl}/portal.php?type=stb&action=get_profile&hd=1&ver=ImageDescription:0.2.18-r23-pub-250;&num_banks=2&stb_type=MAG250&client_type=STB&image_version=218&video_out=hdmi&auth_second_step=1&hw_version=1.7-BD-00&not_valid_token=0&JsHttpRequest=1-xml";
            await GetRequestAsync(portalUrl, mac, token, profileUrl, ct, _ => true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Stalker get_profile failed (non-fatal): {ex.Message}");
        }

        return token;
    }

    /// <summary>
    /// Retrieves the list of genre/category IDs and titles.
    /// </summary>
    public async Task<List<(string Id, string Title)>> GetGenresAsync(
        string portalUrl, string mac, string token, CancellationToken ct = default)
    {
        portalUrl = portalUrl.TrimEnd('/');
        var url = $"{portalUrl}/portal.php?type=itv&action=get_genres&JsHttpRequest=1-xml";

        return await GetRequestAsync(portalUrl, mac, token, url, ct, root =>
        {
            var genres = new List<(string Id, string Title)>();
            foreach (var item in root.GetProperty("js").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString() ?? "";
                var title = item.GetProperty("title").GetString() ?? "";
                genres.Add((id, title));
            }
            return genres;
        });
    }

    /// <summary>
    /// Retrieves all channels across all pages, mapping genre IDs to titles via the genres list.
    /// </summary>
    public async Task<List<StalkerChannelInfo>> GetChannelsAsync(
        string portalUrl, string mac, string token, CancellationToken ct = default)
    {
        portalUrl = portalUrl.TrimEnd('/');
        var allChannels = new List<StalkerChannelInfo>();
        var page = 1;

        while (true)
        {
            var url = $"{portalUrl}/portal.php?type=itv&action=get_ordered_list&genre=*&force_ch_link_check=0&fav=0&sortby=number&hd=0&p={page}&JsHttpRequest=1-xml";

            var pageChannels = await GetRequestAsync(portalUrl, mac, token, url, ct, root =>
            {
                var channels = new List<StalkerChannelInfo>();
                var js = root.GetProperty("js");

                if (js.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        var id = item.GetProperty("id").GetString() ?? "";
                        var name = item.GetProperty("name").GetString() ?? "";
                        var cmd = item.TryGetProperty("cmd", out var cmdProp) ? cmdProp.GetString() ?? "" : "";
                        var genreId = item.TryGetProperty("genre_id", out var gProp) ? gProp.GetString() ?? "" : "";
                        var logo = item.TryGetProperty("logo", out var lProp) ? lProp.GetString() ?? "" : "";

                        channels.Add(new StalkerChannelInfo
                        {
                            Id = id,
                            Name = name,
                            Cmd = cmd,
                            GenreId = genreId,
                            Logo = string.IsNullOrWhiteSpace(logo) ? null : logo
                        });
                    }
                }

                return channels;
            });

            if (pageChannels.Count == 0)
                break;

            allChannels.AddRange(pageChannels);
            page++;
        }

        return allChannels;
    }

    /// <summary>
    /// Resolves a channel's "cmd" field into a real playable URL.
    /// Must be called fresh before each playback — resolved links are short-lived.
    /// </summary>
    public async Task<string> CreateLinkAsync(
        string portalUrl, string mac, string token, string cmd, CancellationToken ct = default)
    {
        portalUrl = portalUrl.TrimEnd('/');
        var encodedCmd = Uri.EscapeDataString(cmd);
        var url = $"{portalUrl}/portal.php?type=itv&action=create_link&cmd={encodedCmd}&forced_storage=0&disable_ad=0&download=0&JsHttpRequest=1-xml";

        return await GetRequestAsync(portalUrl, mac, token, url, ct, root =>
        {
            var raw = root.GetProperty("js").GetProperty("cmd").GetString() ?? "";

            // Strip leading "ffmpeg " prefix if present
            if (raw.StartsWith("ffmpeg ", StringComparison.OrdinalIgnoreCase))
                raw = raw["ffmpeg ".Length..];

            return raw.Trim('`', ' ');
        });
    }

    /// <summary>
    /// Helper: builds a request with Stalker headers, sends it, and parses the JSON response.
    /// </summary>
    private async Task<T> GetRequestAsync<T>(
        string portalUrl, string mac, string? token, string url,
        CancellationToken ct, Func<JsonElement, T> parser)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"mac={mac}; stb_lang=en; timezone=Europe/London");
        request.Headers.Add("User-Agent", UserAgent);
        if (!string.IsNullOrEmpty(token))
            request.Headers.Add("Authorization", $"Bearer {token}");

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return parser(doc.RootElement);
    }
}

public class StalkerChannelInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Cmd { get; set; } = "";
    public string GenreId { get; set; } = "";
    public string? Logo { get; set; }
}
