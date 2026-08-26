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

    [ObservableProperty] private ObservableCollection<string> _categories = new();
    [ObservableProperty] private string? _selectedCategory;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private ObservableCollection<Channel> _filteredChannels = new();
    [ObservableProperty] private Channel? _currentPlayingChannel;
    [ObservableProperty] private string _currentChannelName = "No channel selected";
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _isFullscreen;
    [ObservableProperty] private PlayerState _playerState = PlayerState.Idle;
    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty] private bool _isSettingsOpen;
    [ObservableProperty] private string _customPlaylistUrl = string.Empty;

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

    /// <summary>
    /// Safe wrapper: ensures the action runs on the Avalonia UI thread.
    /// All ObservableCollection modifications and property sets that affect
    /// UI bindings MUST go through this.
    /// </summary>
    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    public async Task InitializeAsync()
    {
        Log.Info("=== InitializeAsync START ===");

        // STEP 1: Show "All" category IMMEDIATELY on the UI thread.
        OnUi(() =>
        {
            ApplyChannels(Array.Empty<Channel>());
            IsLoading = false;
        });
        Log.Info("Initial ApplyChannels dispatched to UI thread");

        // STEP 2: Init DB + load cached channels (async, off UI thread).
        try
        {
            Log.Info("Initializing database...");
            await _databaseService.InitializeAsync();
            Log.Info("Database initialized OK");

            Log.Info("Loading cached channels from DB...");
            var cached = await _playlistService.LoadCachedChannelsAsync();
            Log.Info($"Cached channels loaded: {cached.Count}");

            if (cached.Count > 0)
            {
                OnUi(() => ApplyChannels(cached));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Database/cache load failed", ex);
            OnUi(() => StatusMessage = "Database error — channels may not load");
        }

        // STEP 3: Refresh from network in background.
        _ = RefreshInBackgroundAsync();

        Log.Info("=== InitializeAsync END ===");
    }

    private async Task RefreshInBackgroundAsync()
    {
        try
        {
            OnUi(() =>
            {
                IsRefreshing = true;
                StatusMessage = "Refreshing channels...";
            });
            Log.Info("Background refresh: fetching playlist...");

            var fresh = await _playlistService.RefreshChannelsAsync();
            Log.Info($"Background refresh: fetched {fresh.Count} channels");

            OnUi(() =>
            {
                ApplyChannels(fresh);
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

        Log.Info($"ApplyChannels: {Categories.Count} categories");

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

        Log.Info($"RefreshFilteredChannels: {FilteredChannels.Count} channels");
    }

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

    [RelayCommand]
    private void OpenSettings()
    {
        IsSettingsOpen = true;
        CustomPlaylistUrl = string.Empty;
    }

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private async Task SetCustomUrlAsync()
    {
        if (string.IsNullOrWhiteSpace(CustomPlaylistUrl)) return;
        try
        {
            await _playlistService.SetCustomUrlAsync(CustomPlaylistUrl.Trim());
            IsSettingsOpen = false;
            await RefreshInBackgroundAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to set custom URL", ex);
            StatusMessage = "Failed to set custom URL";
        }
    }

    [RelayCommand]
    private async Task ResetToDefaultAsync()
    {
        try
        {
            await _playlistService.ResetToDefaultAsync();
            IsSettingsOpen = false;
            await RefreshInBackgroundAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to reset playlist", ex);
            StatusMessage = "Failed to reset playlist";
        }
    }

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
