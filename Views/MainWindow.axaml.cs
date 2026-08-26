using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    }

    private void OnMainWindowOpened(object? sender, EventArgs e)
    {
        _vm = DataContext as MainViewModel;

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            UpdateOverlayVisibility(_vm.PlayerState);

            // Pass HWND factory — HWND created lazily on first channel play
            _vm.PlayerService.SetHostHandleFactory(() =>
            {
                CreateVideoHwnd();
                if (_videoHwnd != nint.Zero)
                {
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
        if (_videoHwnd == nint.Zero) return;

        var videoGrid = this.FindControl<Grid>("VideoAreaGrid");
        if (videoGrid is null) return;

        var bounds = videoGrid.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        // Walk up the visual tree to get window-relative coordinates
        var x = bounds.X;
        var y = bounds.Y;
        var visual = (Avalonia.Visual?)videoGrid.Parent;
        while (visual is not null && visual != this)
        {
            x += visual.Bounds.X;
            y += visual.Bounds.Y;
            visual = (Avalonia.Visual?)visual.Parent;
        }

        MoveWindow(_videoHwnd, (int)x, (int)y, (int)bounds.Width, (int)bounds.Height, true);
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

        // Show/hide the mpv HWND based on playback state.
        // HWND is created invisible — only shown when mpv is actively playing.
        // This ensures Avalonia overlays (idle, buffering, error) are always visible
        // when the HWND is not rendering video.
        if (_videoHwnd != nint.Zero)
        {
            if (state == PlayerState.Playing)
                ShowWindow(_videoHwnd, SW_SHOW);
            else
                ShowWindow(_videoHwnd, SW_HIDE);
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
}
