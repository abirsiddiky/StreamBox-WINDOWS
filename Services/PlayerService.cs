using System;
using System.Threading;
using System.Threading.Tasks;
using StreamBox.Models;
using StreamBox.Native;

namespace StreamBox.Services;

public sealed class PlayerService : IDisposable
{
    private long _generation;
    private long _mpvGeneration;
    private readonly SemaphoreSlim _switchGuard = new(1, 1);
    private MpvClient? _mpv;
    private CancellationTokenSource? _bufferingCts;
    private Channel? _currentChannel;
    private Func<nint>? _hostHandleFactory;
    private nint _hostHandle;
    private bool _disposed;

    public event EventHandler<PlayerStateChanged>? StateChanged;

    public Channel? CurrentChannel => _currentChannel;
    public bool IsPlaying { get; private set; }

    /// <summary>
    /// Set a factory that creates/returns the video host HWND on demand.
    /// The HWND is NOT created at startup — only when the first channel plays.
    /// This keeps the player placeholder area visible before playback.
    /// </summary>
    public void SetHostHandleFactory(Func<nint> factory)
    {
        _hostHandleFactory = factory;
    }

    private nint EnsureHostHandle()
    {
        if (_hostHandle == nint.Zero && _hostHandleFactory is not null)
        {
            _hostHandle = _hostHandleFactory();
        }
        return _hostHandle;
    }

    public async Task PlayChannelAsync(Channel channel, CancellationToken cancellationToken = default)
    {
        if (_disposed) return;

        // Lazily create the HWND on first channel play (not at startup)
        var handle = EnsureHostHandle();
        if (handle == nint.Zero)
        {
            Log.Error("PlayChannelAsync called but video host HWND could not be created");
            return;
        }
        _hostHandle = handle;

        var gen = Interlocked.Increment(ref _generation);
        Log.Info($"Switch requested [gen={gen}] channel='{channel.Name}'");

        // CRITICAL: Fire Buffering state IMMEDIATELY so the loading overlay appears
        // before we spend time stopping/disposing the old stream. This eliminates
        // the blank flash between channels.
        _currentChannel = channel;
        IsPlaying = false;
        StateChanged?.Invoke(this, new PlayerStateChanged(gen, PlayerState.Buffering));

        await _switchGuard.WaitAsync(cancellationToken);
        try
        {
            // Cancel any pending buffering timeout from a previous active play request.
            // CRITICAL: This must happen FIRST so no orphaned timer fires.
            CancelBufferingTimeout();

            // Stop and dispose the previous stream OFF the UI thread, with a hard 3s timeout.
            if (_mpv is not null)
            {
                var old = _mpv;
                _mpv = null;

                Log.Info($"Stopping previous stream [gen={gen}]");
                await Task.Run(() =>
                {
                    try { old.Stop(); }
                    catch (Exception ex) { Log.Warn($"Channel switch error: stop failed: {ex.Message}"); }

                    var disposeStart = DateTime.UtcNow;
                    try { old.Dispose(); }
                    catch (Exception ex) { Log.Warn($"Channel switch error: dispose failed: {ex.Message}"); }

                    var elapsed = (DateTime.UtcNow - disposeStart).TotalSeconds;
                    if (elapsed > 2.5)
                    {
                        Log.Warn($"Channel switch: old dispose took {elapsed:F1}s (close to timeout)");
                    }
                }, cancellationToken);

                Log.Info($"Old instance disposed [gen={gen}]");
            }

            // Verify this generation is still current before creating the new instance.
            if (Interlocked.Read(ref _generation) != gen)
            {
                Log.Info($"Generation superseded after dispose, aborting [gen={gen}]");
                return;
            }

            // Create new mpv instance
            try
            {
                _mpv = new MpvClient(_hostHandle);
                _mpv.MpvEventReceived += OnMpvEvent;
                _mpvGeneration = gen;
            }
            catch (Exception ex)
            {
                Log.Error($"Channel switch error: failed to create mpv instance [gen={gen}]", ex);
                IsPlaying = false;
                StateChanged?.Invoke(this, new PlayerStateChanged(gen, PlayerState.Error));
                return;
            }

            // Log per-channel custom headers
            var hasCustomHeaders = channel.UserAgent is not null || channel.ExtraHeaders is { Count: > 0 };
            if (hasCustomHeaders)
            {
                var ua = channel.UserAgent ?? "(default)";
                var cookiePresent = channel.ExtraHeaders?.ContainsKey("cookie") == true ? "present" : "absent";
                Log.Info($"Channel '{channel.Name}' has custom headers: User-Agent={ua}, Cookie=[{cookiePresent}]");
            }

            // Apply per-channel headers
            try
            {
                _mpv.ApplyChannelHeaders(channel);
            }
            catch (Exception ex)
            {
                Log.Warn($"Channel switch error: failed to apply headers [gen={gen}]: {ex.Message}");
            }

            // Load the stream
            Log.Info($"Loading stream for '{channel.Name}' [gen={gen}]");
            try
            {
                _mpv.LoadFile(channel.StreamUrl);
            }
            catch (Exception ex)
            {
                Log.Error($"Channel switch error: failed to load stream [gen={gen}]", ex);
                IsPlaying = false;
                StateChanged?.Invoke(this, new PlayerStateChanged(gen, PlayerState.Error));
                return;
            }

            _currentChannel = channel;
            // Buffering state already fired at switch start — no need to fire again here.

            // Start buffering timeout ONLY as part of an active, generation-tagged play request.
            StartBufferingTimeout(gen);

            Log.Info($"Playing [gen={gen}]");
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"Channel switch cancelled [gen={gen}]");
        }
        catch (Exception ex)
        {
            Log.Error($"Channel switch error [gen={gen}]", ex);
            IsPlaying = false;
            StateChanged?.Invoke(this, new PlayerStateChanged(gen, PlayerState.Error));
        }
        finally
        {
            _switchGuard.Release();
        }

        Log.Info($"Switch complete [gen={gen}]");
    }

    public void StopPlayback()
    {
        if (_disposed) return;

        var gen = Interlocked.Read(ref _generation);
        Log.Info($"StopPlayback requested [gen={gen}]");

        CancelBufferingTimeout();

        if (_mpv is not null)
        {
            try { _mpv.Stop(); }
            catch (Exception ex) { Log.Warn($"StopPlayback: mpv stop failed: {ex.Message}"); }
        }

        IsPlaying = false;
        _currentChannel = null;
        StateChanged?.Invoke(this, new PlayerStateChanged(gen, PlayerState.Idle));
    }

    public void RetryCurrentChannel()
    {
        if (_currentChannel is not null)
        {
            _ = PlayChannelAsync(_currentChannel);
        }
    }

    private void OnMpvEvent(object? sender, MpvEventArgs e)
    {
        // Capture the generation this MpvClient belongs to — NOT the global _generation counter.
        var mpvGen = Volatile.Read(ref _mpvGeneration);
        var currentGen = Volatile.Read(ref _generation);

        // This event is from a stale MpvClient — discard silently.
        if (mpvGen != currentGen) return;

        try
        {
            switch (e.Kind)
            {
                case MpvEventKind.FileLoaded:
                    // Re-check generation — switch may have happened while event was queued.
                    if (Volatile.Read(ref _mpvGeneration) != Volatile.Read(ref _generation)) return;
                    CancelBufferingTimeout();
                    IsPlaying = true;
                    Log.Info($"File loaded [gen={mpvGen}]");
                    StateChanged?.Invoke(this, new PlayerStateChanged(mpvGen, PlayerState.Playing));
                    break;

                case MpvEventKind.EndFile:
                    CancelBufferingTimeout();

                    var reasonCode = e.ReasonCode ?? -1;
                    var errorCode = e.ErrorCode ?? 0;

                    // MpvEndFileReason: 0=Eof, 2=Stop, 3=Quit, 4=Error, 5=Redirect
                    if (reasonCode == 4) // Error
                    {
                        if (Volatile.Read(ref _mpvGeneration) != Volatile.Read(ref _generation)) return;
                        Log.Warn($"EndFile with error (code={errorCode}) [gen={mpvGen}] — attempting self-healing");
                        IsPlaying = false;
                        SelfHeal();
                    }
                    else
                    {
                        IsPlaying = false;
                        Log.Info($"EndFile reason={reasonCode} [gen={mpvGen}] (clean stop)");
                        StateChanged?.Invoke(this, new PlayerStateChanged(mpvGen, PlayerState.Idle));
                    }
                    break;

                case MpvEventKind.PropertyChange:
                    if (e.PropertyName == "idle-active" && e.FlagValue == true)
                    {
                        if (Volatile.Read(ref _mpvGeneration) != Volatile.Read(ref _generation)) return;
                        CancelBufferingTimeout();
                    }
                    break;

                case MpvEventKind.Shutdown:
                    if (Volatile.Read(ref _mpvGeneration) != Volatile.Read(ref _generation)) return;
                    Log.Warn($"mpv shutdown event [gen={mpvGen}] — self-healing");
                    CancelBufferingTimeout();
                    IsPlaying = false;
                    SelfHeal();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Channel switch error in event handler [gen={mpvGen}]", ex);
        }
    }

    private void SelfHeal()
    {
        try
        {
            // Only heal if there's no active MpvClient and the host handle is valid.
            if (_mpv is not null || _hostHandle == nint.Zero) return;

            Log.Info("Self-healing: creating new mpv instance");
            _mpv = new MpvClient(_hostHandle);
            _mpv.MpvEventReceived += OnMpvEvent;
            _mpvGeneration = Volatile.Read(ref _generation);
            Log.Info("Self-healing: new mpv instance created");
        }
        catch (Exception ex)
        {
            Log.Error("Self-healing failed", ex);
            StateChanged?.Invoke(this, new PlayerStateChanged(Volatile.Read(ref _generation), PlayerState.Error));
        }
    }

    private void StartBufferingTimeout(long generation)
    {
        CancelBufferingTimeout();

        _bufferingCts = new CancellationTokenSource();
        var ct = _bufferingCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);

                // Timeout fired — only act if generation hasn't changed.
                if (Interlocked.Read(ref _generation) == generation)
                {
                    Log.Warn($"Buffering timeout (30s) [gen={generation}] — stream failed");
                    IsPlaying = false;
                    StateChanged?.Invoke(this, new PlayerStateChanged(generation, PlayerState.Error));
                }
                else
                {
                    Log.Info($"Buffering timeout fired for stale gen={generation} (current={Volatile.Read(ref _generation)}), discarding");
                }
            }
            catch (OperationCanceledException)
            {
                // Normal — timeout was cancelled because stream loaded or switch happened.
            }
            catch (Exception ex)
            {
                Log.Warn($"Buffering timeout task error: {ex.Message}");
            }
        }, CancellationToken.None);
    }

    private void CancelBufferingTimeout()
    {
        try
        {
            _bufferingCts?.Cancel();
            _bufferingCts?.Dispose();
            _bufferingCts = null;
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CancelBufferingTimeout();

        if (_mpv is not null)
        {
            try { _mpv.Stop(); } catch { }
            try { _mpv.Dispose(); } catch { }
            _mpv = null;
        }

        _switchGuard.Dispose();
    }
}

public sealed record PlayerStateChanged(long Generation, PlayerState State);

public enum PlayerState
{
    Idle,
    Buffering,
    Playing,
    Error
}
