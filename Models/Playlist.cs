namespace StreamBox.Models;

public sealed class Playlist
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SourceKind { get; set; } = "Builtin";
    public string? SourceValue { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }
}
