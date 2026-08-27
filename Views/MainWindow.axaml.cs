using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using StreamBox.Models;
using StreamBox.Services;
using StreamBox.ViewModels;

namespace StreamBox.Views;

public partial class MainWindow : Window
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    private nint _videoHwnd = nint.Zero;
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnMainWindowOpened;
        Resized += OnWindowResized;
        Closed += OnMainWindowClosed;
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F)
        {
            ToggleFullscreen();
        }
    }

    private void ToggleFullscreen()
    {
        var sidebar = this.FindControl<Border>("SidebarBorder");
        var videoGrid = this.FindControl<Grid>("VideoAreaGrid");

        if (sidebar is null || videoGrid is null) return;

        if (WindowState == WindowState.FullScreen)
        {
            // Exit fullscreen: restore sidebar
            sidebar.IsVisible = true;
            sidebar.Width = 330;
            WindowState = WindowState.Normal;
        }
        else
        {
            // Enter fullscreen: hide sidebar, video fills entire window
            sidebar.IsVisible = false;
            sidebar.Width = 0;
            WindowState = WindowState.FullScreen;
        }

        // Reposition HWND after layout settles — fire at multiple intervals
        // to catch the layout update regardless of timing
        void Reposition() => Dispatcher.UIThread.Post(() => RepositionVideoHwnd(), DispatcherPriority.Loaded);
        Reposition();
        Task.Delay(50).ContinueWith(_ => Reposition());
        Task.Delay(150).ContinueWith(_ => Reposition());
    }

    private void OnMainWindowOpened(object? sender, EventArgs e)
    {
        _vm = DataContext as MainViewModel;

        // Log window dimensions at open time
        var parentHwnd = this.TryGetPlatformHandle()?.Handle ?? nint.Zero;
        if (parentHwnd != nint.Zero)
        {
            GetWindowRect(parentHwnd, out var winRect);
            GetClientRect(parentHwnd, out var cliRect);
            Log.Info($"[DIAG] Window opened — WindowRect: {winRect.Right - winRect.Left}x{winRect.Bottom - winRect.Top}, ClientRect: {cliRect.Right}x{cliRect.Bottom}, RenderScaling: {this.RenderScaling}");
        }

        // Log VideoAreaGrid bounds after first layout
        var videoGrid = this.FindControl<Grid>("VideoAreaGrid");
        if (videoGrid is not null)
        {
            Log.Info($"[DIAG] VideoAreaGrid at open — Bounds: {videoGrid.Bounds.Width}x{videoGrid.Bounds.Height}");
            videoGrid.LayoutUpdated += (_, _) =>
            {
                Log.Info($"[DIAG] VideoAreaGrid LayoutUpdated — Bounds: {videoGrid.Bounds.Width}x{videoGrid.Bounds.Height}");
                RepositionVideoHwnd();
            };
            videoGrid.PropertyChanged += (_, args) =>
            {
                if (args.Property.Name == "Bounds")
                {
                    Log.Info($"[DIAG] VideoAreaGrid Bounds changed — {videoGrid.Bounds.Width}x{videoGrid.Bounds.Height}");
                }
            };
        }

        // Log sidebar dimensions
        var sidebar = this.FindControl<Border>("SidebarBorder");
        if (sidebar is not null)
        {
            Log.Info($"[DIAG] Sidebar at open — Width: {sidebar.Width}, Bounds: {sidebar.Bounds.Width}, IsVisible: {sidebar.IsVisible}");
        }

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            UpdateOverlayVisibility(_vm.PlayerState);

            _vm.PlayerService.SetHostHandleFactory(() =>
            {
                CreateVideoHwnd();
                if (_videoHwnd != nint.Zero)
                {
                    Log.Info($"[DIAG] HWND factory called — _videoHwnd={_videoHwnd}");
                    // Immediate reposition
                    Dispatcher.UIThread.Post(() => RepositionVideoHwnd(), DispatcherPriority.Loaded);
                }
                return _videoHwnd;
            });

            Log.Info("MainWindow opened — starting InitializeAsync");
            _ = _vm.InitializeAsync().ContinueWith(t =>
            {
                if (t.Exception is not null)
                    Log.Error("InitializeAsync failed", t.Exception);
                else
                    Log.Info("startup complete");
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
        }
    }

    private void CreateVideoHwnd()
    {
        var parentHwnd = this.TryGetPlatformHandle()?.Handle ?? nint.Zero;
        if (parentHwnd == nint.Zero)
        {
            Log.Error("Failed to get parent HWND from Avalonia window");
            return;
        }

        _videoHwnd = CreateWindowExW(
            0, "STATIC", string.Empty,
            WS_CHILD | WS_CLIPCHILDREN,
            0, 0, 100, 100,
            parentHwnd, nint.Zero, nint.Zero, nint.Zero);

        if (_videoHwnd == nint.Zero)
        {
            Log.Error("Failed to create child HWND for video");
            return;
        }

        Log.Info($"Video HWND created: {_videoHwnd}");
    }

    private void RepositionVideoHwnd()
    {
        if (_videoHwnd == nint.Zero)
        {
            Log.Info("[DIAG] RepositionVideoHwnd: _videoHwnd is Zero — skipping");
            return;
        }

        var parentHwnd = this.TryGetPlatformHandle()?.Handle ?? nint.Zero;
        if (parentHwnd == nint.Zero)
        {
            Log.Info("[DIAG] RepositionVideoHwnd: parentHwnd is Zero — skipping");
            return;
        }

        GetClientRect(parentHwnd, out var clientRect);
        GetWindowRect(parentHwnd, out var windowRect);

        var sidebar = this.FindControl<Border>("SidebarBorder");
        var sidebarWidth = (sidebar is not null && sidebar.IsVisible) ? (int)(sidebar.Width * this.RenderScaling) : 0;

        var videoW = clientRect.Right - clientRect.Left - sidebarWidth;
        var videoH = clientRect.Bottom - clientRect.Top;

        Log.Info($"[DIAG] RepositionVideoHwnd: parentHwnd={parentHwnd}, clientRect={clientRect.Right}x{clientRect.Bottom}, windowRect={windowRect.Right - windowRect.Left}x{windowRect.Bottom - windowRect.Top}, sidebar.Width={sidebar?.Width}, sidebar.Visible={sidebar?.IsVisible}, sidebarWidth(scaled)={sidebarWidth}, videoW={videoW}, videoH={videoH}, RenderScaling={this.RenderScaling}");

        if (videoW <= 0 || videoH <= 0)
        {
            Log.Info("[DIAG] RepositionVideoHwnd: videoW or videoH <= 0 — skipping");
            return;
        }

        MoveWindow(_videoHwnd, 0, 0, videoW, videoH, true);
        Log.Info($"[DIAG] RepositionVideoHwnd: MoveWindow called with ({0}, {0}, {videoW}, {videoH})");

        // Verify after move
        GetWindowRect(_videoHwnd, out var hwndRect);
        Log.Info($"[DIAG] After MoveWindow: HWND WindowRect = {hwndRect.Right - hwndRect.Left}x{hwndRect.Bottom - hwndRect.Top}");
    }

    private void OnWindowResized(object? sender, WindowResizedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => RepositionVideoHwnd(), DispatcherPriority.Loaded);
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (_videoHwnd != nint.Zero)
        {
            try { DestroyWindow(_videoHwnd); } catch { }
            _videoHwnd = nint.Zero;
        }

        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.PlayerService.StopPlayback();
            _vm.PlayerService.Dispose();
        }
    }

    // ── Overlay Management ──

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PlayerState) && _vm is not null)
        {
            Dispatcher.UIThread.Post(() => UpdateOverlayVisibility(_vm.PlayerState));
        }
    }

    private void UpdateOverlayVisibility(PlayerState state)
    {
        var vm = _vm;
        if (vm is null) return;

        var hasChannel = vm.CurrentPlayingChannel is not null;

        IdleOverlay.IsVisible = !hasChannel && state == PlayerState.Idle;
        BufferingOverlay.IsVisible = state == PlayerState.Buffering;
        ErrorOverlay.IsVisible = state == PlayerState.Error;
        ChannelNameStrip.IsVisible = hasChannel && state != PlayerState.Idle;

        if (_videoHwnd != nint.Zero)
        {
            if (state == PlayerState.Playing)
                ShowWindow(_videoHwnd, SW_SHOW);
            else
                ShowWindow(_videoHwnd, SW_HIDE);
        }
    }

    // ── Playlist Selection ──

    private void PlaylistButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is Playlist playlist && _vm is not null)
        {
            _vm.SelectedPlaylist = playlist;
            UpdatePlaylistActionVisibility(playlist);
        }
    }

    private void UpdatePlaylistActionVisibility(Playlist playlist)
    {
        // Hide Export and Delete for the built-in/default playlist
        var isDefault = playlist.SourceKind == "Default";
        ExportButton.IsVisible = !isDefault;
        DeleteButton.IsVisible = !isDefault;
    }

    private void AddPlaylistButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.AddPlaylistName = string.Empty;
        _vm.AddPlaylistUrl = string.Empty;
        _vm.AddPlaylistFromFile = false;
        AddPlaylistOverlay.IsVisible = true;
    }

    private void CancelAddPlaylist_Click(object? sender, RoutedEventArgs e)
    {
        AddPlaylistOverlay.IsVisible = false;
    }

    private async void ConfirmAddPlaylist_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (string.IsNullOrWhiteSpace(_vm.AddPlaylistName)) return;

        AddPlaylistOverlay.IsVisible = false;
        await _vm.ConfirmAddPlaylistCommand.ExecuteAsync(null);
    }

    private void CancelRenamePlaylist_Click(object? sender, RoutedEventArgs e)
    {
        RenamePlaylistOverlay.IsVisible = false;
    }

    private void ConfirmRenamePlaylist_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (string.IsNullOrWhiteSpace(_vm.RenamePlaylistName)) return;

        RenamePlaylistOverlay.IsVisible = false;
        ((System.Windows.Input.ICommand)_vm.ConfirmRenamePlaylistCommand).Execute(null);
    }

    // ── Playlist Actions (Refresh / Rename / Export / Remove) ──

    private void PlaylistAction_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _vm is null) return;
        if (_vm.SelectedPlaylist is null) return;

        var action = button.Tag as string;
        switch (action)
        {
            case "Refresh":
                _ = _vm.RefreshPlaylistCommand.ExecuteAsync(_vm.SelectedPlaylist);
                break;

            case "Rename":
                _vm.RenamePlaylistName = _vm.SelectedPlaylist.Name;
                RenamePlaylistOverlay.IsVisible = true;
                break;

            case "Export":
                _ = ExportCurrentPlaylistAsync();
                break;

            case "Remove":
                _ = _vm.RemovePlaylistCommand.ExecuteAsync(_vm.SelectedPlaylist);
                break;
        }
    }

    private async Task ExportCurrentPlaylistAsync()
    {
        if (_vm?.SelectedPlaylist is null) return;

        var playlistName = _vm.SelectedPlaylist.Name;
        var safeFileName = string.Join("_",
            playlistName.Split(System.IO.Path.GetInvalidFileNameChars()));

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Playlist as M3U",
            SuggestedFileName = $"{safeFileName}.m3u",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("M3U Playlist Files") { Patterns = new[] { "*.m3u" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        if (file is null) return;

        try
        {
            var m3uContent = await _vm.ExportPlaylistAsync(_vm.SelectedPlaylist.Id, null);
            if (m3uContent is not null)
            {
                await System.IO.File.WriteAllTextAsync(file.Path.LocalPath, m3uContent);
                Log.Info($"Playlist exported to: {file.Path.LocalPath}");
                _vm.StatusMessage = $"Playlist exported to {System.IO.Path.GetFileName(file.Path.LocalPath)}";
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Export failed: {ex.Message}");
            if (_vm is not null) _vm.StatusMessage = "Export failed";
        }
    }

    // ── Add Playlist: Browse File ──

    private async void BrowseFileButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select M3U Playlist File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("M3U Playlist Files") { Patterns = new[] { "*.m3u", "*.m3u8", "*.txt" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        if (files.Count > 0 && _vm is not null)
        {
            _vm.AddPlaylistUrl = files[0].Path.LocalPath;
        }
    }

    // ── Categories ──

    private void CategoryButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string category && _vm is not null)
        {
            _vm.SelectedCategory = category;
        }
    }

    // ── Channel List ──

    private void ChannelListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox listBox) return;
        if (listBox.SelectedItem is Channel channel && _vm is not null)
        {
            _ = _vm.PlayChannelCommand.ExecuteAsync(channel);
        }
    }

    // ── P/Invoke ──

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(
        nint hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }
}
