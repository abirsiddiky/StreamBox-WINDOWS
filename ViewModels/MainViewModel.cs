using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamBox.Models;
using StreamBox.Services;

namespace StreamBox.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DatabaseService _databaseService;
    private readonly PlaylistService _playlistService;
    private readonly PlayerService _playerService;
    private List<Channel> _allChannels = new();

    // ── Category / Search ──

    [ObservableProperty] private ObservableCollection<string> _categories = new();
    [ObservableProperty] private string? _selectedCategory;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private ObservableCollection<Channel> _filteredChannels = new();

    // ── Player ──

    [ObservableProperty] private Channel? _currentPlayingChannel;
    [ObservableProperty] private string _currentChannelName = "No channel selected";
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _isFullscreen;
    [ObservableProperty] private PlayerState _playerState = PlayerState.Idle;
    [ObservableProperty] private string _statusMessage = string.Empty;

    // ── Playlist Management ──

    [ObservableProperty] private ObservableCollection<Playlist> _playlists = new();
    [ObservableProperty] private Playlist? _selectedPlaylist;

    // ── Add Playlist Dialog ──

    [ObservableProperty] private bool _isAddPlaylistOpen;
    [ObservableProperty] private string _addPlaylistName = string.Empty;
    [ObservableProperty] private string _addPlaylistUrl = string.Empty;
    [ObservableProperty] private bool _addPlaylistFromFile;

    // ── Rename Playlist Dialog ──

    [ObservableProperty] private bool _isRenamePlaylistOpen;
    [ObservableProperty] private string _renamePlaylistName = string.Empty;
    private long _renamingPlaylistId;

    public PlayerService PlayerService => _playerService;

    public MainViewModel(
        DatabaseService databaseService,
        PlaylistService playlistService,
        PlayerService playerService)
    {
        _databaseService = databaseService;
        _playlistService = playlistService;
        _playerService = playerService;
        _playerService.StateChanged += OnPlayerStateChanged;
    }

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    // ── Initialization ──

    public async Task InitializeAsync()
    {
        Log.Info("=== InitializeAsync START ===");

        // Show empty UI immediately
        OnUi(() =>
        {
            ApplyChannels(Array.Empty<Channel>());
            IsLoading = false;
        });

        try
        {
            Log.Info("Initializing database...");
            await _databaseService.InitializeAsync();
            Log.Info("Database initialized OK");

            // Load all playlists
            var playlists = await _playlistService.LoadPlaylistsAsync();
            OnUi(() =>
            {
                Playlists.Clear();
                foreach (var p in playlists)
                {
                    Playlists.Add(p);
                }
            });

            // Determine active playlist
            long? activeId = await _playlistService.GetActivePlaylistIdAsync();
            if (activeId is null && playlists.Count > 0)
            {
                activeId = playlists[0].Id;
                await _playlistService.SetActivePlaylistIdAsync(activeId.Value);
            }

            // If no playlists exist, create the default "Built-in" playlist
            if (playlists.Count == 0)
            {
                var builtInId = await _playlistService.AddPlaylistAsync("Built-in", "Default", null);
                activeId = builtInId;
                await _playlistService.SetActivePlaylistIdAsync(builtInId);
                playlists = await _playlistService.LoadPlaylistsAsync();
                OnUi(() =>
                {
                    Playlists.Clear();
                    foreach (var p in playlists)
                    {
                        Playlists.Add(p);
                    }
                });
            }

            // Select the active playlist
            var activePlaylist = playlists.FirstOrDefault(p => p.Id == activeId);
            if (activePlaylist is null && playlists.Count > 0)
            {
                activePlaylist = playlists[0];
            }

            if (activePlaylist is not null)
            {
                OnUi(() => SelectedPlaylist = activePlaylist);

                // Load cached channels for the active playlist
                var cached = await _playlistService.LoadCachedChannelsAsync(activePlaylist.Id);
                if (cached.Count > 0)
                {
                    OnUi(() => ApplyChannels(cached));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Database/cache load failed", ex);
            OnUi(() => StatusMessage = "Database error — channels may not load");
        }

        // Background refresh of the active playlist
        _ = RefreshActivePlaylistAsync();

        Log.Info("=== InitializeAsync END ===");
    }

    // ── Playlist Selection ──

    partial void OnSelectedPlaylistChanged(Playlist? value)
    {
        if (value is null) return;

        _ = SelectPlaylistAsync(value);
    }

    private async Task SelectPlaylistAsync(Playlist playlist)
    {
        try
        {
            await _playlistService.SetActivePlaylistIdAsync(playlist.Id);

            // Load cached channels for this playlist
            var cached = await _playlistService.LoadCachedChannelsAsync(playlist.Id);
            OnUi(() => ApplyChannels(cached));
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to select playlist '{playlist.Name}': {ex.Message}");
            StatusMessage = $"Failed to load playlist '{playlist.Name}'";
        }
    }

    // ── Playlist Management Commands ──

    [RelayCommand]
    private void OpenAddPlaylist()
    {
        AddPlaylistName = string.Empty;
        AddPlaylistUrl = string.Empty;
        AddPlaylistFromFile = false;
        IsAddPlaylistOpen = true;
    }

    [RelayCommand]
    private void CloseAddPlaylist()
    {
        IsAddPlaylistOpen = false;
    }

    [RelayCommand]
    private async Task ConfirmAddPlaylistAsync()
    {
        var name = AddPlaylistName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusMessage = "Playlist name is required";
            return;
        }

        IsAddPlaylistOpen = false;

        try
        {
            string sourceKind;
            string? sourceValue;

            if (AddPlaylistFromFile)
            {
                sourceKind = "CustomFile";
                sourceValue = AddPlaylistUrl; // File path from code-behind file picker
            }
            else
            {
                var url = AddPlaylistUrl.Trim();
                if (string.IsNullOrWhiteSpace(url))
                {
                    sourceKind = "Default";
                    sourceValue = null;
                }
                else
                {
                    sourceKind = "CustomUrl";
                    sourceValue = url;
                }
            }

            var newId = await _playlistService.AddPlaylistAsync(name, sourceKind, sourceValue);
            var playlists = await _playlistService.LoadPlaylistsAsync();

            OnUi(() =>
            {
                Playlists.Clear();
                foreach (var p in playlists)
                {
                    Playlists.Add(p);
                }

                var newlyAdded = playlists.FirstOrDefault(p => p.Id == newId);
                if (newlyAdded is not null)
                {
                    SelectedPlaylist = newlyAdded;
                }
            });

            // Refresh the new playlist
            await RefreshPlaylistByIdAsync(newId);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to add playlist", ex);
            StatusMessage = $"Failed to add playlist: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RemovePlaylistAsync(Playlist? playlist)
    {
        if (playlist is null) return;

        var wasActive = SelectedPlaylist?.Id == playlist.Id;

        try
        {
            await _playlistService.RemovePlaylistAsync(playlist.Id);
            var playlists = await _playlistService.LoadPlaylistsAsync();

            OnUi(() =>
            {
                Playlists.Clear();
                foreach (var p in playlists)
                {
                    Playlists.Add(p);
                }
            });

            // If we removed the active playlist, switch to the first remaining one
            if (wasActive)
            {
                if (playlists.Count > 0)
                {
                    OnUi(() => SelectedPlaylist = playlists[0]);
                }
                else
                {
                    OnUi(() =>
                    {
                        SelectedPlaylist = null;
                        ApplyChannels(Array.Empty<Channel>());
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to remove playlist '{playlist.Name}': {ex.Message}");
            StatusMessage = $"Failed to remove playlist";
        }
    }

    [RelayCommand]
    private void OpenRenamePlaylist(Playlist? playlist)
    {
        if (playlist is null) return;
        _renamingPlaylistId = playlist.Id;
        RenamePlaylistName = playlist.Name;
        IsRenamePlaylistOpen = true;
    }

    [RelayCommand]
    private void CloseRenamePlaylist()
    {
        IsRenamePlaylistOpen = false;
    }

    [RelayCommand]
    private async Task ConfirmRenamePlaylistAsync()
    {
        var newName = RenamePlaylistName.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        IsRenamePlaylistOpen = false;

        try
        {
            await _playlistService.RenamePlaylistAsync(_renamingPlaylistId, newName);
            var playlists = await _playlistService.LoadPlaylistsAsync();

            OnUi(() =>
            {
                Playlists.Clear();
                foreach (var p in playlists)
                {
                    Playlists.Add(p);
                }

                // Restore selection
                var renamed = playlists.FirstOrDefault(p => p.Id == _renamingPlaylistId);
                if (renamed is not null)
                {
                    SelectedPlaylist = renamed;
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to rename playlist: {ex.Message}");
            StatusMessage = "Failed to rename playlist";
        }
    }

    [RelayCommand]
    private async Task RefreshPlaylistAsync(Playlist? playlist)
    {
        if (playlist is null) return;
        await RefreshPlaylistByIdAsync(playlist.Id);
    }

    private async Task RefreshPlaylistByIdAsync(long playlistId)
    {
        try
        {
            OnUi(() => IsRefreshing = true);
            var channels = await _playlistService.RefreshPlaylistChannelsAsync(playlistId);

            // If this is the currently selected playlist, update the view
            if (SelectedPlaylist?.Id == playlistId)
            {
                OnUi(() => ApplyChannels(channels));
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Refresh playlist failed (id={playlistId}): {ex.Message}");
            OnUi(() => StatusMessage = "Refresh failed — check network or URL");
        }
        finally
        {
            OnUi(() => IsRefreshing = false);
        }
    }

    /// <summary>
    /// Exports a playlist to M3U file. Returns the file path chosen by the user, or null if cancelled.
    /// This is invoked from code-behind which handles the file dialog.
    /// </summary>
    public async Task<string?> ExportPlaylistAsync(long playlistId, string? suggestedFileName)
    {
        try
        {
            return await _playlistService.ExportPlaylistToM3uAsync(playlistId);
        }
        catch (Exception ex)
        {
            Log.Error($"Export playlist failed (id={playlistId}): {ex.Message}");
            StatusMessage = "Export failed";
            return null;
        }
    }

    // ── Background Refresh ──

    private async Task RefreshActivePlaylistAsync()
    {
        if (SelectedPlaylist is null) return;

        try
        {
            OnUi(() =>
            {
                IsRefreshing = true;
                StatusMessage = "Refreshing channels...";
            });

            var channels = await _playlistService.RefreshPlaylistChannelsAsync(SelectedPlaylist.Id);
            OnUi(() =>
            {
                ApplyChannels(channels);
                StatusMessage = string.Empty;
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Background refresh failed: {ex.Message}");
            OnUi(() => StatusMessage = "Refresh failed — check network");
        }
        finally
        {
            OnUi(() =>
            {
                IsLoading = false;
                IsRefreshing = false;
            });
        }
    }

    // ── Channel Display ──

    private void ApplyChannels(IReadOnlyList<Channel> channels)
    {
        Log.Info($"ApplyChannels: {channels.Count} channels");

        _allChannels = channels.ToList();

        var groups = _allChannels
            .Select(c => c.GroupTitle)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Categories.Clear();
        Categories.Add("All");
        foreach (var g in groups)
        {
            Categories.Add(g);
        }

        SelectedCategory ??= "All";
        RefreshFilteredChannels();
    }

    partial void OnSelectedCategoryChanged(string? value)
    {
        RefreshFilteredChannels();
    }

    partial void OnSearchTextChanged(string value)
    {
        RefreshFilteredChannels();
    }

    private void RefreshFilteredChannels()
    {
        var query = _allChannels.AsEnumerable();

        if (!string.Equals(SelectedCategory, "All", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(SelectedCategory))
        {
            query = query.Where(c =>
                string.Equals(c.GroupTitle, SelectedCategory, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText.Trim();
            query = query.Where(c =>
                c.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        FilteredChannels = new ObservableCollection<Channel>(query);
        StatusMessage = FilteredChannels.Count == 0 && _allChannels.Count > 0
            ? "No channels match"
            : string.Empty;
    }

    // ── Playback ──

    [RelayCommand]
    private async Task PlayChannelAsync(Channel? channel)
    {
        if (channel is null) return;
        CurrentPlayingChannel = channel;
        CurrentChannelName = channel.Name;
        await _playerService.PlayChannelAsync(channel);
    }

    [RelayCommand]
    private void RetryPlayback() => _playerService.RetryCurrentChannel();

    [RelayCommand]
    private void ToggleFullscreen() => IsFullscreen = !IsFullscreen;

    private void OnPlayerStateChanged(object? sender, PlayerStateChanged e)
    {
        OnUi(() =>
        {
            PlayerState = e.State;
            switch (e.State)
            {
                case PlayerState.Buffering: StatusMessage = "Buffering..."; break;
                case PlayerState.Playing: StatusMessage = string.Empty; break;
                case PlayerState.Error: StatusMessage = "Stream failed"; break;
                case PlayerState.Idle:
                    if (CurrentPlayingChannel is not null) StatusMessage = string.Empty;
                    break;
            }
        });
    }
}
