using System.Collections.Generic;

namespace StreamBox.Models;

public sealed class Channel
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string GroupTitle { get; set; } = "All";
    public string? LogoUrl { get; set; }
    public string StreamUrl { get; set; } = string.Empty;
    public string? UserAgent { get; set; }
    public Dictionary<string, string>? ExtraHeaders { get; set; }
    public int SortOrder { get; set; }
}
