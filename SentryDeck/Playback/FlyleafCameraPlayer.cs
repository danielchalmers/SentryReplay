using System.ComponentModel;
using System.Windows.Media;
using FlyleafLib;
using FlyleafLib.Controls.WPF;
using FlyleafLib.MediaPlayer;

namespace SentryDeck;

/// <summary>
/// Flyleaf-backed player for one camera view.
/// </summary>
internal sealed class FlyleafCameraPlayer : ICameraPlayer
{
    private readonly FlyleafHost _host;
    private readonly Player _player;
    private bool _isDisposed;
    private bool _isOpen;
    private bool _isStopping;

    public FlyleafCameraPlayer(FlyleafHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _player = new Player(CreateConfig());

        // All shortcuts are app-wide and act on every camera at once; Flyleaf's default bindings (space, arrows, …) would pause/seek only the player whose surface has focus.
        // Must run AFTER the Player ctor: KeysConfig.SetPlayer force-loads the defaults into any empty binding list, so clearing the config up front is undone (and RemoveAll on a fresh config NREs, since Keys is null until SetPlayer runs).
        // Belt-and-braces with FlyleafHost.KeyBindings=None on the hosts.
        _player.Config.Player.KeyBindings.RemoveAll();

        _host.Player = _player;

        _player.PlaybackStopped += OnPlaybackStopped;
        _player.PropertyChanged += OnPropertyChanged;
    }

    public event EventHandler Opened;
    public event EventHandler Ended;
    public event EventHandler<CameraPlaybackFailedEventArgs> Failed;
    public event EventHandler<CameraPositionChangedEventArgs> PositionChanged;

    public bool IsOpen => _isOpen;

    public TimeSpan Position => TimeSpan.FromTicks(_player.CurTime);

    public double Speed
    {
        get => _player.Speed;
        set
        {
            if (value > 0)
            {
                _player.Speed = value;
            }
        }
    }

    public async Task<bool> OpenAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ThrowIfDisposed();

        _isStopping = false;

        try
        {
            var result = await Task.Run(() => _player.Open(
                path,
                defaultPlaylistItem: true,
                defaultVideo: true,
                defaultAudio: false,
                defaultSubtitles: false,
                forceSubtitles: false));

            _isOpen = result.Success;
            if (result.Success)
            {
                Opened?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                RaiseFailed(result.Error);
            }

            return result.Success;
        }
        catch (Exception ex)
        {
            _isOpen = false;
            Failed?.Invoke(this, new CameraPlaybackFailedEventArgs(ex));
            return false;
        }
    }

    public Task PlayAsync()
    {
        ThrowIfDisposed();
        _player.Play();
        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        ThrowIfDisposed();
        _player.Pause();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        return Task.Run(StopAndClose);
    }

    public Task CloseAsync()
    {
        return Task.Run(StopAndClose);
    }

    public Task SeekAsync(TimeSpan position, bool accurate = true)
    {
        ThrowIfDisposed();

        if (!_isOpen)
        {
            return Task.CompletedTask;
        }

        var milliseconds = (int)Math.Clamp(position.TotalMilliseconds, 0, int.MaxValue);

        if (accurate)
        {
            _player.SeekAccurate(milliseconds);
        }
        else
        {
            // Keyframe seek: jumps to the nearest preceding keyframe instead of decoding forward to the exact frame.
            // Far cheaper, so it's used for live scrubbing while dragging the seek bar; the final release seek always goes through the accurate path above.
            _player.Seek(milliseconds, forward: false);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Fallback frame rate for the backward-step seek when the open stream doesn't report one.
    /// </summary>
    private const double FallbackStepFps = 30.0;

    /// <summary>
    /// Safety factor applied to the backward-step target so PTS rounding can't make the accurate seek land back on the frame currently displayed (accurate seeks present the frame at or before the target, so overshooting slightly INTO the previous frame is what we want).
    /// </summary>
    private const double BackwardStepPtsGuard = 1.1;

    public Task StepFrameAsync(bool forward)
    {
        ThrowIfDisposed();

        if (!_isOpen)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            if (forward)
            {
                _player.ShowFrameNext();
            }
            else
            {
                // Do NOT "simplify" this back to Flyleaf's ShowFramePrev.
                // On our ffconcat (FFmpeg concat demuxer) playlists it is a silent no-op -- CurTime never moves, nothing throws -- and on FlyleafLib 3.10.4 it also poisoned the decoder so the NEXT ShowFrameNext jumped ahead by 0.5s+ instead of one frame (verified against real footage, 2026-07).
                // Re-measured on 3.11.3 after its seek and frame-stepping lock fix (2026-08): the poisoning is gone, and a following ShowFrameNext advances exactly one frame again, but the backward step itself still moved 0 of 8 attempts on footage with no audio track, which is the footage this app opens.
                // It does step correctly on 3.11.3 when the media happens to carry an audio track, so a quick test on the wrong sample file will suggest this workaround is unnecessary; it is not.
                // Backward stepping is therefore a small accurate seek: one frame duration back, padded by a PTS-rounding guard so the seek reliably presents the PREVIOUS frame rather than re-presenting the current one.
                // Accurate seeks are proven reliable on these playlists, including while paused.
                var fps = _player.Video?.FPS ?? 0;
                if (fps <= 0 || double.IsNaN(fps))
                {
                    fps = FallbackStepFps;
                }

                var frameTicks = (long)(TimeSpan.TicksPerSecond / fps);
                var targetTicks = Math.Max(0, _player.CurTime - (long)(BackwardStepPtsGuard * frameTicks));
                _player.SeekAccurate((int)(targetTicks / TimeSpan.TicksPerMillisecond));
            }
        });
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _player.PlaybackStopped -= OnPlaybackStopped;
        _player.PropertyChanged -= OnPropertyChanged;
        _host.Player = null;
        _player.Dispose();
    }

    /// <summary>
    /// Audio is disabled on every camera, so a clip that carries an audio track is opened video-only.
    /// Dashcam recordings have no audio, the app exposes no volume or mute control, and four to six players sharing one timeline would each render their own copy of any track that did turn up.
    /// It also keeps the app clear of FlyleafLib 3.11's audio filter graph, which has two defects this app would otherwise have to work around: under LoadProfile.Main the audio decoder's static constructor dies on the missing avfilter and drops that camera to roughly an eighth of real-time playback, and a speed change overlapping a seek wedges the seek thread inside avfilter_graph_free and freezes the UI thread behind it.
    /// Turning audio back on means dealing with both again, and moving FlyleafRuntime to LoadProfile.Filters at the same time.
    /// Trimmed exports are unaffected: ClipExporter stream-copies whatever the source holds, audio included.
    /// </summary>
    private static Config CreateConfig()
    {
        var config = new Config
        {
            Player =
            {
                AutoPlay = false,
                SeekAccurate = true,
            },
            Video =
            {
                BackColor = Colors.Black,
            },
            Audio =
            {
                Enabled = false,
            },
            Subtitles =
            {
                Enabled = false,
            },
            Data =
            {
                Enabled = false,
            },
        };

        // Clip playlists are ffconcat files with absolute paths; "safe=0" tells FFmpeg's concat demuxer to allow them (it refuses absolute/outside-directory paths by default).
        config.Demuxer.FormatOpt["safe"] = "0";

        return config;
    }

    private void StopAndClose()
    {
        if (_isDisposed)
            return;

        try
        {
            _isStopping = true;
            _player.Stop();
        }
        finally
        {
            _isOpen = false;
        }
    }

    private void OnPlaybackStopped(object sender, PlaybackStoppedArgs e)
    {
        if (_isStopping || _isDisposed)
            return;

        if (!string.IsNullOrWhiteSpace(e.Error))
        {
            _isOpen = false;
            RaiseFailed(e.Error);
            return;
        }

        if (_player.Status == Status.Ended)
        {
            // Reaching end-of-stream does NOT close the media: Flyleaf keeps the demuxer open at EOF, so playback can still be replayed, scrubbed, or frame-stepped from the parked end position.
            // Clearing _isOpen here made every open-player-gated operation (PlayAsync's replay, SeekAsync, StepFrameAsync) a silent no-op once a clip finished, freezing it on its last frame while the controller still reported IsPlaying.
            // Leave it open; the real close happens on StopAndClose (user Stop / clip change / dispose).
            Ended?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (_isDisposed || !_isOpen || e.PropertyName != nameof(Player.CurTime))
            return;

        PositionChanged?.Invoke(this, new CameraPositionChangedEventArgs(TimeSpan.FromTicks(_player.CurTime)));
    }

    private void RaiseFailed(string error)
    {
        var exception = new InvalidOperationException(string.IsNullOrWhiteSpace(error)
            ? "Flyleaf failed to play the media."
            : error);
        Failed?.Invoke(this, new CameraPlaybackFailedEventArgs(exception));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}
