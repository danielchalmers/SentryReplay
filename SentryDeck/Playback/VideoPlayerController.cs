using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using FlyleafLib.Controls.WPF;
using Serilog;

namespace SentryDeck;

/// <summary>
/// Coordinates Flyleaf camera players, playing each camera's chunk sequence as a single continuous ffconcat playlist so clip playback never stalls at chunk boundaries.
/// </summary>
public sealed partial class VideoPlayerController : ObservableObject, IDisposable
{
    /// <summary>
    /// How far short of <see cref="Duration"/> the front player's position can be when it ends before we treat that as a premature stop (a corrupt/truncated chunk) rather than a normal clip completion.
    /// Probed chunk durations are exact, so a genuine end lands within about a frame of Duration; the imprecise case is a fallback-estimated (unprobeable) chunk, and files we can't probe are exactly the files likely to be corrupt.
    /// </summary>
    private static readonly TimeSpan PrematureEndTolerance = TimeSpan.FromSeconds(3);

    private const int MaxRecoveryAttemptsPerClip = 3;

    /// <summary>
    /// Shown when a clip's files carry Tesla's 2026.20+ dashcam encryption instead of plain MP4s.
    /// The keys live behind the owner's Tesla account, so pointing at the in-car toggle and Tesla's own viewer is the most useful thing the app can do.
    /// </summary>
    internal const string EncryptedClipMessage =
        "This clip appears to be encrypted by the vehicle (Tesla software 2026.20 and later encrypts dashcam recordings by default). To record playable clips, turn off Controls > Safety > Encrypt Dashcam Recordings. Already-encrypted clips can be viewed at dashcam.tesla.com.";

    /// <summary>
    /// One-shot guard for the reopen/seek race after a recovery: how long to wait after issuing the resume seek before verifying the front player actually landed near the target, and how far below the target the reported position may sit before the seek is reissued once.
    /// </summary>
    private static readonly TimeSpan DefaultPostRecoverySeekVerifyDelay = TimeSpan.FromMilliseconds(500);

    private readonly Func<CancellationToken, Task> _postRecoverySeekVerifyDelay;

    private static readonly TimeSpan PostRecoverySeekTolerance = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How far before a clip's event moment a freshly opened clip starts playing, so the approach to the incident is visible instead of dropping the viewer straight onto the trigger frame.
    /// Mirrors the in-car player, which since Tesla's 2024 Holiday Update opens each recording at the event rather than at the top of the buffer.
    /// A clip with no locatable event opens at the start as before.
    /// </summary>
    private static readonly TimeSpan EventLeadIn = TimeSpan.FromSeconds(10);

    private readonly string _primaryCamera;
    private readonly ICameraPlayer _primaryPlayer;
    private readonly IReadOnlyDictionary<string, ICameraPlayer> _players;
    private readonly IClipMediaSourceBuilder _mediaSourceBuilder;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly HashSet<int> _excludedChunkIndices = [];

    // _excludedChunkIndices is mutated under the operation lock (a clip change clears it, recovery adds to it) but is ALSO read outside that lock on a Flyleaf callback thread (recovery maps a failure position back to an original chunk index).
    // Guard every access with this lock so a concurrent clear can't throw "Collection was modified" out of an async void player handler.
    private readonly Lock _excludedChunkIndicesLock = new();
    private CancellationTokenSource _playbackCts;
    private bool _isDisposed;
    private bool _isOpeningMedia;
    private long _activeRequestId;
    private long _currentMediaRequestId;
    private CamClip _openedClip;
    private ClipMediaSource _openedMediaSource;
    private int _recoveryAttempts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlayPause))]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private TimeSpan _position;

    [ObservableProperty]
    private TimeSpan _duration;

    [ObservableProperty]
    private string _errorMessage;

    [ObservableProperty]
    private double _playbackSpeed = 1.0;

    [ObservableProperty]
    private bool _isMediaOpen;

    /// <param name="players">Camera players keyed by camera name.
    /// Every present camera is played; the clip decides which are actually opened.</param> <param name="primaryCamera">The camera that drives the shared clock, is required to open, and anchors corrupt-chunk recovery (front on a real Tesla).</param>
    /// <param name="postRecoverySeekVerifyDelay">Waits before the post-recovery seek is verified.
    /// Defaults to a real delay; overridable for tests, which would otherwise pay it in wall-clock time on every recovery test and could not control when the verification runs.</param>
    public VideoPlayerController(
        IReadOnlyDictionary<string, ICameraPlayer> players,
        string primaryCamera,
        IClipMediaSourceBuilder mediaSourceBuilder = null,
        Func<CancellationToken, Task> postRecoverySeekVerifyDelay = null)
    {
        ArgumentNullException.ThrowIfNull(players);
        ArgumentException.ThrowIfNullOrEmpty(primaryCamera);

        if (players.Count == 0)
        {
            throw new ArgumentException("At least one camera player is required.", nameof(players));
        }

        if (!players.TryGetValue(primaryCamera, out var primaryPlayer) || primaryPlayer is null)
        {
            throw new ArgumentException($"The primary camera '{primaryCamera}' has no player.", nameof(primaryCamera));
        }

        _primaryCamera = primaryCamera;
        _primaryPlayer = primaryPlayer;
        _players = players;
        _mediaSourceBuilder = mediaSourceBuilder ?? new FfconcatMediaSourceBuilder();
        _postRecoverySeekVerifyDelay = postRecoverySeekVerifyDelay ?? (token => Task.Delay(DefaultPostRecoverySeekVerifyDelay, token));

        Playlist = new ClipPlaylist();
        Playlist.CurrentClipChanged += OnCurrentClipChanged;
        Playlist.PlaylistChanged += OnPlaylistChanged;

        foreach (var player in _players.Values)
        {
            player.Opened += OnPlayerOpened;
            player.Ended += OnPlayerEnded;
            player.Failed += OnPlayerFailed;
            player.PositionChanged += OnPositionChanged;
        }
    }

    /// <summary>
    /// Creates the app's Flyleaf-backed playback controller from one surface per camera.
    /// The primary camera (front when present) drives the timeline; playback is video-only on every camera.
    /// </summary>
    public static VideoPlayerController Create(IReadOnlyList<(string Camera, FlyleafHost Host)> cameras)
    {
        ArgumentNullException.ThrowIfNull(cameras);

        if (cameras.Count == 0)
        {
            throw new ArgumentException("At least one camera is required.", nameof(cameras));
        }

        var primaryCamera = cameras.Any(camera => camera.Camera == CameraNames.Front)
            ? CameraNames.Front
            : cameras[0].Camera;

        var players = cameras.ToDictionary(
            camera => camera.Camera,
            camera => (ICameraPlayer)new FlyleafCameraPlayer(camera.Host));

        return new VideoPlayerController(players, primaryCamera);
    }

    public ClipPlaylist Playlist { get; }

    public CamClip CurrentClip => Playlist.CurrentClip;

    /// <summary>
    /// The media source backing the currently opened clip, or null when nothing is open.
    /// Exposed (read-only) so callers can map wall-clock instants (e.g. event timestamps) onto the actual playing media time via <see cref="ClipMediaSource.ToMediaTime"/> and read <see cref="ClipMediaSource.GapPositions"/>, rather than re-deriving a timeline estimate of their own.
    /// Changes alongside <see cref="IsMediaOpen"/> and <see cref="Duration"/>.
    /// </summary>
    public ClipMediaSource OpenedMediaSource => _openedMediaSource;

    public bool CanPlayPause => CurrentClip is not null && !IsLoading;

    public bool CanGoNext => Playlist.HasNext;

    public bool CanGoPrevious => Playlist.HasPrevious;

    public async Task PlayAsync()
    {
        var clip = CurrentClip;
        if (clip is null)
            return;

        if (IsMediaOpen && _openedClip == clip)
        {
            if (Duration > TimeSpan.Zero && Position >= Duration - TimeSpan.FromMilliseconds(250))
            {
                await SeekAsync(TimeSpan.Zero);
            }

            await RunSerializedPlaybackOperationAsync(async _ =>
            {
                if (IsMediaOpen && _openedClip == clip)
                {
                    await PlayOpenPlayersAsync();
                    IsPlaying = true;
                }
            });

            return;
        }

        var requestId = BeginNewRequest();
        await PlayInternalAsync(requestId, clip);
    }

    public async Task PauseAsync()
    {
        await RunSerializedPlaybackOperationAsync(async _ =>
        {
            Log.Debug(
                "Pausing playback. ClipName={ClipName}; ClipPath={ClipPath}; Position={Position}",
                CurrentClip?.Name,
                CurrentClip?.FullPath,
                Position);
            await PauseOpenPlayersAsync();
            IsPlaying = false;
        });
    }

    public async Task TogglePlayPauseAsync()
    {
        if (IsPlaying)
        {
            await PauseAsync();
        }
        else
        {
            await PlayAsync();
        }
    }

    public async Task StopAsync()
    {
        BeginNewRequest();
        await RunSerializedPlaybackOperationAsync(async _ =>
        {
            Log.Debug(
                "Stopping playback. ClipName={ClipName}; ClipPath={ClipPath}; Position={Position}",
                CurrentClip?.Name,
                CurrentClip?.FullPath,
                Position);
            CancelAndDisposePlaybackCts();
            await StopPlaybackInternalAsync(resetPlaybackState: true);
        });
    }

    public Task SeekAsync(TimeSpan position) => SeekInternalAsync(position, accurate: true);

    /// <summary>
    /// Like <see cref="SeekAsync"/> but issues fast keyframe seeks to every open player instead of accurate ones -- intended to be called repeatedly and cheaply while the seek bar thumb is being dragged, so the video keeps up in near-real-time.
    /// Same clamping and serialized-operation infrastructure as <see cref="SeekAsync"/>; only the seek mode differs.
    /// </summary>
    public Task ScrubSeekAsync(TimeSpan position) => SeekInternalAsync(position, accurate: false);

    private async Task SeekInternalAsync(TimeSpan position, bool accurate)
    {
        if (CurrentClip is null || _isDisposed || !IsMediaOpen || Duration <= TimeSpan.Zero)
            return;

        try
        {
            await RunSerializedPlaybackOperationAsync(async _ =>
            {
                if (!IsMediaOpen || _openedClip != CurrentClip)
                {
                    return;
                }

                var clampedPosition = Clamp(position, TimeSpan.Zero, Duration);
                await SeekOpenPlayersAsync(clampedPosition, accurate);
                Position = clampedPosition;
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(
                ex,
                "Seek error. ClipName={ClipName}; ClipPath={ClipPath}; RequestedPosition={RequestedPosition}; Accurate={Accurate}",
                CurrentClip?.Name,
                CurrentClip?.FullPath,
                position,
                accurate);
            ErrorMessage = $"Seek error: {ex.Message}";
        }
    }

    /// <summary>
    /// Steps every open player one frame forward or backward -- for frame-by-frame incident review.
    /// Stepping only makes sense paused, so playback is paused first if active; all open players are stepped in the same direction to keep the four cameras in sync (they share the same frame rate).
    /// </summary>
    public async Task StepFrameAsync(bool forward)
    {
        if (CurrentClip is null || _isDisposed || !IsMediaOpen)
            return;

        try
        {
            await RunSerializedPlaybackOperationAsync(async _ =>
            {
                if (!IsMediaOpen || _openedClip != CurrentClip)
                {
                    return;
                }

                if (IsPlaying)
                {
                    await PauseOpenPlayersAsync();
                    IsPlaying = false;
                }

                var positionBeforeStep = _primaryPlayer.Position;

                foreach (var player in _players.Values.Where(player => player.IsOpen))
                {
                    await player.StepFrameAsync(forward);
                }

                // Flyleaf raises PositionChanged (via CurTime) when a stepped frame shows, even while paused, so Position normally updates on its own via OnPositionChanged.
                // This is a belt-and-suspenders sync from the front player in case that event doesn't fire for a given step.
                Position = Clamp(_primaryPlayer.Position, TimeSpan.Zero, Duration);

                Log.Debug(
                    "Stepped frame. Forward={Forward}; PositionBefore={PositionBefore}; PositionAfter={PositionAfter}; ClipName={ClipName}",
                    forward,
                    positionBeforeStep,
                    _primaryPlayer.Position,
                    CurrentClip?.Name);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(
                ex,
                "Frame step error. ClipName={ClipName}; ClipPath={ClipPath}; Forward={Forward}",
                CurrentClip?.Name,
                CurrentClip?.FullPath,
                forward);
            ErrorMessage = $"Frame step error: {ex.Message}";
        }
    }

    public async Task NextAsync()
    {
        if (!Playlist.HasNext)
            return;

        await PrepareForClipChangeAsync();
        Playlist.MoveNext();
    }

    public async Task PreviousAsync()
    {
        if (!Playlist.HasPrevious)
            return;

        await PrepareForClipChangeAsync();
        Playlist.MovePrevious();
    }

    public async Task GoToClipAsync(CamClip clip)
    {
        if (clip is null || clip == Playlist.CurrentClip || !Playlist.Clips.Contains(clip))
            return;

        await PrepareForClipChangeAsync();
        Playlist.MoveTo(clip);
    }

    public async Task GoToClipAsync(int index)
    {
        if (index == Playlist.CurrentIndex || index < 0 || index >= Playlist.Clips.Count)
            return;

        await PrepareForClipChangeAsync();
        Playlist.MoveTo(index);
    }

    public async Task LoadClipsAsync(IEnumerable<CamClip> clips)
    {
        await StopAsync();
        Playlist.SetClips(clips);
    }

    public void LoadClips(IEnumerable<CamClip> clips)
    {
        Playlist.SetClips(clips);
    }

    /// <summary>
    /// Drops a single clip from the playlist so Next/Previous navigation stays aligned with a trimmed clip list (e.g. after the user deletes a clip).
    /// Does not touch what's playing; when the removed clip is the current one the caller is responsible for having stopped it.
    /// </summary>
    public void RemoveClip(CamClip clip) => Playlist.RemoveClip(clip);

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        CancelAndDisposePlaybackCts();

        Playlist.CurrentClipChanged -= OnCurrentClipChanged;
        Playlist.PlaylistChanged -= OnPlaylistChanged;

        foreach (var player in _players.Values)
        {
            player.Opened -= OnPlayerOpened;
            player.Ended -= OnPlayerEnded;
            player.Failed -= OnPlayerFailed;
            player.PositionChanged -= OnPositionChanged;
            player.Dispose();
        }

        _operationLock.Dispose();
    }

    private long BeginNewRequest()
    {
        return Interlocked.Increment(ref _activeRequestId);
    }

    private bool IsRequestActive(long requestId)
    {
        return requestId == Volatile.Read(ref _activeRequestId);
    }

    private async Task RunSerializedPlaybackOperationAsync(Func<CancellationToken, Task> operation, bool replacePlaybackCts = false)
    {
        if (_isDisposed)
            return;

        var acquired = false;

        try
        {
            await _operationLock.WaitAsync();
            acquired = true;

            var token = replacePlaybackCts
                ? ReplacePlaybackCts().Token
                : _playbackCts?.Token ?? CancellationToken.None;

            await operation(token);
        }
        catch (ObjectDisposedException) when (_isDisposed)
        {
            // The controller was disposed (window closed) while this operation was in flight, so the lock or a player is already gone.
            // We're shutting down; nothing to recover.
        }
        finally
        {
            if (acquired)
            {
                // Dispose() can run on the UI thread while this operation is mid-flight and then dispose the semaphore; releasing it afterwards would throw.
                // Benign at shutdown.
                try
                {
                    _operationLock.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }

    private CancellationTokenSource ReplacePlaybackCts()
    {
        CancelAndDisposePlaybackCts();
        _playbackCts = new CancellationTokenSource();
        return _playbackCts;
    }

    private void CancelAndDisposePlaybackCts()
    {
        var cts = _playbackCts;
        _playbackCts = null;

        if (cts is null)
            return;

        cts.Cancel();
        cts.Dispose();
    }

    private async Task PrepareForClipChangeAsync()
    {
        BeginNewRequest();
        CancelAndDisposePlaybackCts();
        ErrorMessage = null;
        IsLoading = true;

        await Task.Yield();

        await RunSerializedPlaybackOperationAsync(async _ =>
        {
            await StopPlaybackInternalAsync(resetPlaybackState: true, clearLoading: false);
        });
    }

    private async Task PlayInternalAsync(long requestId, CamClip clip)
    {
        if (clip is null)
            return;

        ErrorMessage = null;
        IsLoading = true;

        try
        {
            await RunSerializedPlaybackOperationAsync(async ct =>
            {
                if (!IsRequestActive(requestId) || clip != CurrentClip)
                    return;

                await StopPlaybackInternalAsync(resetPlaybackState: false);

                // A freshly selected clip starts with no known-bad chunks and a clean recovery budget, regardless of what happened on the previously playing clip.
                lock (_excludedChunkIndicesLock)
                {
                    _excludedChunkIndices.Clear();
                }

                _recoveryAttempts = 0;

                if (clip.Chunks.Count == 0)
                {
                    Log.Warning(
                        "Clip has no playable chunks. ClipName={ClipName}; ClipPath={ClipPath}",
                        clip.Name,
                        clip.FullPath);
                    ErrorMessage = "No playable footage found.";
                    return;
                }

                var mediaSource = await Task.Run(() => _mediaSourceBuilder.Build(clip), ct);
                Duration = mediaSource.Duration;

                Log.Information(
                    "Starting clip playback. ClipName={ClipName}; ClipPath={ClipPath}; ClipIndex={ClipIndex}; ClipCount={ClipCount}; ChunkCount={ChunkCount}; Duration={Duration}; RequestId={RequestId}",
                    clip.Name,
                    clip.FullPath,
                    Playlist.CurrentIndex,
                    Playlist.Clips.Count,
                    clip.Chunks.Count,
                    Duration,
                    requestId);
                await OpenClipInternalAsync(clip, mediaSource, playAfterOpen: true, requestId, ct);
            }, replacePlaybackCts: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(
                ex,
                "Playback error. ClipName={ClipName}; ClipPath={ClipPath}; RequestId={RequestId}",
                clip.Name,
                clip.FullPath,
                requestId);
            ErrorMessage = $"Playback error: {ex.Message}";
        }
        finally
        {
            if (IsRequestActive(requestId))
            {
                IsLoading = false;
            }
        }
    }

    private async Task StopPlaybackInternalAsync(bool resetPlaybackState, bool clearLoading = true)
    {
        Volatile.Write(ref _currentMediaRequestId, 0);
        await StopAndClosePlayersAsync();

        IsMediaOpen = false;
        _openedClip = null;
        _openedMediaSource = null;
        OnPropertyChanged(nameof(OpenedMediaSource));

        if (resetPlaybackState)
        {
            if (clearLoading)
            {
                IsLoading = false;
            }

            IsPlaying = false;
            Position = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
        }
    }

    private async Task StopAndClosePlayersAsync()
    {
        foreach (var player in _players.Values)
        {
            try
            {
                await player.StopAsync();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to stop media player during cleanup");
            }

            try
            {
                await player.CloseAsync();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to close media player during cleanup");
            }
        }
    }

    private async Task OpenClipInternalAsync(
        CamClip clip,
        ClipMediaSource mediaSource,
        bool playAfterOpen,
        long requestId,
        CancellationToken cancellationToken)
    {
        if (clip is null || mediaSource is null)
            return;

        if (!mediaSource.CameraPlaylistPaths.ContainsKey(_primaryCamera))
        {
            Log.Warning(
                "Cannot open clip because the primary camera is missing. PrimaryCamera={PrimaryCamera}; ClipName={ClipName}; ClipPath={ClipPath}; Cameras={Cameras}; RequestId={RequestId}",
                _primaryCamera,
                clip.Name,
                clip.FullPath,
                mediaSource.CameraPlaylistPaths.Keys.Order().ToArray(),
                requestId);

            // A fully encrypted clip lands here too: the builder probes every chunk's front file, finds no readable moov in any of them, and excludes them all, indistinguishable from "no footage" without sniffing the files themselves.
            ErrorMessage = EncryptedClipDetector.LooksEncrypted(clip)
                ? EncryptedClipMessage
                : $"No {CameraNames.DisplayName(_primaryCamera)} camera footage found.";
            return;
        }

        _isOpeningMedia = true;

        try
        {
            await StopAndClosePlayersAsync();
            IsMediaOpen = false;

            cancellationToken.ThrowIfCancellationRequested();

            var primaryOpened = await OpenCameraPlayerAsync(
                _primaryCamera,
                _primaryPlayer,
                mediaSource,
                required: true,
                requestId,
                cancellationToken);

            if (!primaryOpened)
            {
                IsPlaying = false;
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            _openedClip = clip;
            _openedMediaSource = mediaSource;
            OnPropertyChanged(nameof(OpenedMediaSource));

            // The builder may have dropped unreadable chunks on its own; fold those into the exclusion set so position-to-chunk mapping during recovery stays aligned with the shrunken timeline.
            // They are not recovery attempts and don't count toward the cap.
            if (mediaSource.AutoExcludedChunkIndices.Count > 0)
            {
                Log.Warning(
                    "Builder auto-excluded unreadable chunks. ClipName={ClipName}; ClipPath={ClipPath}; AutoExcludedChunkIndices={AutoExcludedChunkIndices}",
                    clip.Name,
                    clip.FullPath,
                    mediaSource.AutoExcludedChunkIndices);
                lock (_excludedChunkIndicesLock)
                {
                    _excludedChunkIndices.UnionWith(mediaSource.AutoExcludedChunkIndices);
                }
            }

            IsMediaOpen = _primaryPlayer.IsOpen;

            ApplyPlaybackSpeed();

            Position = TimeSpan.Zero;
            Volatile.Write(ref _currentMediaRequestId, requestId);

            if (playAfterOpen)
            {
                // Get the user watching video as soon as the front camera (the authoritative, required source) is ready, rather than gating first-frame on the slowest of four opens.
                // Side cameras join in progress once their own opens complete, below.
                await _primaryPlayer.PlayAsync();
                IsPlaying = true;

                // Position events can now flow: the front player is genuinely playing, so this is no different from a fully-completed open as far as Ended/Failed/PositionChanged are concerned.
                // Side opens below still run under _isOpeningMedia's other protections indirectly -- those handlers only special-case the front player.
                _isOpeningMedia = false;

                // Jump to just before the event moment on open, matching the in-car player.
                // Seek AFTER Play (never before): a seek issued while paused right after open can be swallowed by the player, whereas seeks during active playback are reliable -- the same ordering the recovery resume relies on.
                // The secondary cameras join at this position below, since they read _primaryPlayer.Position after the seek lands.
                var eventStartPosition = ResolveEventStartPosition(clip, mediaSource);
                if (eventStartPosition > TimeSpan.Zero)
                {
                    await _primaryPlayer.SeekAsync(eventStartPosition);
                    Position = eventStartPosition;
                }

                await OpenAndJoinSecondaryCamerasAsync(mediaSource, requestId, cancellationToken);
            }
            else
            {
                await OpenSecondaryCamerasAsync(mediaSource, requestId, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                await PauseOpenPlayersAsync();
                IsPlaying = false;
            }

            Log.Information(
                "Opened clip playback. ClipName={ClipName}; ClipPath={ClipPath}; Duration={Duration}; IsPlaying={IsPlaying}; Cameras={Cameras}; RequestId={RequestId}",
                clip.Name,
                clip.FullPath,
                mediaSource.Duration,
                IsPlaying,
                mediaSource.CameraPlaylistPaths.Keys.Order().ToArray(),
                requestId);
        }
        catch (OperationCanceledException)
        {
            await StopAndClosePlayersAsync();
            IsMediaOpen = false;
            throw;
        }
        finally
        {
            _isOpeningMedia = false;
        }
    }

    /// <summary>
    /// The media-time position a freshly opened clip should start playing at: <see cref="EventLeadIn"/> before its event moment when one is locatable within the built media, or <see cref="TimeSpan.Zero"/> otherwise (no event metadata, or an event that falls outside the recorded footage).
    /// Uses the same wall-clock-to-media-time mapping as the seek-bar event marker (<see cref="ClipMediaSource.ToMediaTime"/>), so the auto-jump lands consistently with the marker the user sees.
    /// </summary>
    private static TimeSpan ResolveEventStartPosition(CamClip clip, ClipMediaSource mediaSource)
    {
        var camEvent = clip.Event;
        if (camEvent is null || camEvent.Timestamp == default)
        {
            return TimeSpan.Zero;
        }

        if (mediaSource.ToMediaTime(camEvent.Timestamp) is not { } eventMediaTime)
        {
            return TimeSpan.Zero;
        }

        var start = eventMediaTime - EventLeadIn;
        return start < TimeSpan.Zero ? TimeSpan.Zero : start;
    }

    /// <summary>
    /// Opens the three non-front cameras in parallel (as before) but, unlike the pre-playback path, joins each one in as soon as ITS OWN open completes rather than waiting for all three: seeks it to the front player's current (live) position and starts it playing, so the user isn't blocked on the slowest secondary camera to see the front feed.
    /// </summary>
    private async Task OpenAndJoinSecondaryCamerasAsync(
        ClipMediaSource mediaSource,
        long requestId,
        CancellationToken cancellationToken)
    {
        var joinTasks = _players
            .Where(cameraPlayer => !ReferenceEquals(cameraPlayer.Value, _primaryPlayer))
            .Select(cameraPlayer => OpenAndJoinSecondaryCameraAsync(
                cameraPlayer.Key,
                cameraPlayer.Value,
                mediaSource,
                requestId,
                cancellationToken));

        await Task.WhenAll(joinTasks);
    }

    /// <summary>
    /// Opens a single secondary camera and, once open, joins it into the already-playing front stream: seeks to the front's current position and plays.
    /// Performs a single-shot correction afterward if the join seek's own latency let the gap grow further, so the camera doesn't visibly trail the front by much more than one seek's worth of drift.
    /// </summary>
    private async Task OpenAndJoinSecondaryCameraAsync(
        string camera,
        ICameraPlayer player,
        ClipMediaSource mediaSource,
        long requestId,
        CancellationToken cancellationToken)
    {
        var opened = await OpenCameraPlayerAsync(camera, player, mediaSource, required: false, requestId, cancellationToken);

        if (!opened || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var joinPosition = _primaryPlayer.Position;

        Log.Debug(
            "Joining secondary camera to in-progress playback. Camera={Camera}; JoinPosition={JoinPosition}; RequestId={RequestId}",
            camera,
            joinPosition,
            requestId);

        await player.SeekAsync(joinPosition);
        await player.PlayAsync();

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // The seek above takes some non-zero time, during which the front kept playing; do one single-shot correction if the gap grew meaningfully rather than looping/polling.
        var driftAfterJoin = _primaryPlayer.Position - joinPosition;
        if (driftAfterJoin > TimeSpan.FromMilliseconds(250))
        {
            var correctedPosition = _primaryPlayer.Position;

            Log.Debug(
                "Secondary camera join drifted; reissuing seek. Camera={Camera}; DriftAfterJoin={DriftAfterJoin}; CorrectedPosition={CorrectedPosition}; RequestId={RequestId}",
                camera,
                driftAfterJoin,
                correctedPosition,
                requestId);

            await player.SeekAsync(correctedPosition);
        }
    }

    /// <summary>
    /// Opens the three non-front cameras in parallel without playing or seeking them -- used by the recovery path, which stays paused until the caller positions and plays everything.
    /// </summary>
    private async Task OpenSecondaryCamerasAsync(
        ClipMediaSource mediaSource,
        long requestId,
        CancellationToken cancellationToken)
    {
        var secondaryOpenTasks = _players
            .Where(cameraPlayer => !ReferenceEquals(cameraPlayer.Value, _primaryPlayer))
            .Select(cameraPlayer => OpenCameraPlayerAsync(
                cameraPlayer.Key,
                cameraPlayer.Value,
                mediaSource,
                required: false,
                requestId,
                cancellationToken));

        await Task.WhenAll(secondaryOpenTasks);
    }

    private async Task<bool> OpenCameraPlayerAsync(
        string camera,
        ICameraPlayer player,
        ClipMediaSource mediaSource,
        bool required,
        long requestId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!mediaSource.CameraPlaylistPaths.TryGetValue(camera, out var playlistPath) || !File.Exists(playlistPath))
        {
            Log.Debug(
                "Camera playlist not available. Camera={Camera}; RequestId={RequestId}",
                camera,
                requestId);

            if (required)
            {
                ErrorMessage = $"Failed to open {CameraNames.DisplayName(camera)} camera video.";
            }

            return false;
        }

        var opened = await player.OpenAsync(playlistPath);
        if (!opened)
        {
            var messageTemplate = required
                ? "Failed to open required camera video. Camera={Camera}; File={File}; RequestId={RequestId}"
                : "Failed to open secondary camera video. Camera={Camera}; File={File}; RequestId={RequestId}";

            Log.Warning(
                messageTemplate,
                camera,
                playlistPath,
                requestId);

            if (required)
            {
                ErrorMessage = $"Failed to open {CameraNames.DisplayName(camera)} camera video.";
            }

            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        player.Speed = PlaybackSpeed;

        return true;
    }

    private async Task PlayOpenPlayersAsync()
    {
        ApplyPlaybackSpeed();

        foreach (var player in _players.Values.Where(player => player.IsOpen))
        {
            await player.PlayAsync();
        }
    }

    private async Task PauseOpenPlayersAsync()
    {
        foreach (var player in _players.Values.Where(player => player.IsOpen))
        {
            await player.PauseAsync();
        }
    }

    private async Task SeekOpenPlayersAsync(TimeSpan offset, bool accurate = true)
    {
        foreach (var player in _players.Values.Where(player => player.IsOpen))
        {
            await player.SeekAsync(offset, accurate);
        }
    }

    private void ApplyPlaybackSpeed()
    {
        foreach (var (camera, player) in _players)
        {
            try
            {
                player.Speed = PlaybackSpeed;
            }
            catch (Exception ex)
            {
                Log.Debug(
                    ex,
                    "Failed to apply playback speed. Camera={Camera}; PlaybackSpeed={PlaybackSpeed}",
                    camera,
                    PlaybackSpeed);
            }
        }
    }

    partial void OnPlaybackSpeedChanged(double value)
    {
        if (value <= 0)
        {
            Log.Warning("Ignoring invalid playback speed. PlaybackSpeed={PlaybackSpeed}", value);
            PlaybackSpeed = 1.0;
            return;
        }

        ApplyPlaybackSpeed();
    }

    private void OnCurrentClipChanged(object sender, CamClip clip)
    {
        OnPropertyChanged(nameof(CurrentClip));
        OnPropertyChanged(nameof(CanPlayPause));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrevious));

        if (clip is not null)
        {
            Log.Debug(
                "Current clip changed. ClipName={ClipName}; ClipPath={ClipPath}; ClipIndex={ClipIndex}; ClipCount={ClipCount}",
                clip.Name,
                clip.FullPath,
                Playlist.CurrentIndex,
                Playlist.Clips.Count);
            var requestId = BeginNewRequest();
            _ = PlayInternalAsync(requestId, clip);
        }
    }

    private void OnPlaylistChanged(object sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CurrentClip));
        OnPropertyChanged(nameof(CanPlayPause));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrevious));
    }

    private void OnPlayerOpened(object sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _primaryPlayer))
        {
            IsMediaOpen = true;
        }
    }

    private async void OnPlayerEnded(object sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _primaryPlayer) || _isOpeningMedia)
            return;

        var wasPlaying = IsPlaying;
        IsPlaying = false;

        // Deliberately NOT gated on IsLoading: the front plays (and can end or die on a corrupt first chunk) while the secondary-camera joins are still in flight, and IsLoading stays true until that whole open completes.
        // The request-id check above already filters the transitions IsLoading used to guard (clip changes zero _currentMediaRequestId first).
        var mediaRequestId = Volatile.Read(ref _currentMediaRequestId);
        if (mediaRequestId == 0 || mediaRequestId != Volatile.Read(ref _activeRequestId))
        {
            return;
        }

        if (_openedMediaSource is not null && Duration - Position > PrematureEndTolerance)
        {
            await RecoverFromPrematureEndAsync(wasPlaying);
            return;
        }

        // Deliberately no auto-advance to the next clip: each clip is its own incident, and the most likely follow-up to watching one is replaying it, not being yanked to the next.
        // Playback simply parks at the end.
        // The media stays open (IsMediaOpen unchanged) so the scrubber and frame-step remain usable to review the final moments, and PlayAsync replays from the start when pressed at the end.
        // Next/Previous remain explicit user actions.
        Position = Duration;
    }

    /// <summary>
    /// Handles the front player ending (or failing) well short of <see cref="Duration"/>, which means the concat demuxer hit a corrupt/truncated chunk and stopped early rather than reaching the real end of the clip.
    /// Recovery is probe-first: rebuild with the current exclusions and let the builder's per-file probe find the culprit (the demuxer reads ahead of the presentation position, so the failure position can sit inside a healthy chunk); only when the probe finds nothing new is the chunk containing the failure position excluded.
    /// Gives up after <see cref="MaxRecoveryAttemptsPerClip"/> attempts on the same clip.
    /// </summary>
    private async Task RecoverFromPrematureEndAsync(bool wasPlaying)
    {
        var clip = _openedClip;
        var mediaSource = _openedMediaSource;

        if (clip is null || mediaSource is null)
        {
            return;
        }

        var failurePosition = Clamp(Position, TimeSpan.Zero, Duration);

        // Find the last chunk boundary at or before the failure position; that's the chunk the failure happened inside, and where playback should resume.
        // Map it from the (possibly already-shrunk) opened timeline back to the original clip's chunk index.
        var badChunkTimelineIndex = 0;
        for (var i = 0; i < mediaSource.ChunkStarts.Count; i++)
        {
            if (mediaSource.ChunkStarts[i] <= failurePosition)
            {
                badChunkTimelineIndex = i;
            }
            else
            {
                break;
            }
        }

        var resumePosition = mediaSource.ChunkStarts.Count > badChunkTimelineIndex
            ? mediaSource.ChunkStarts[badChunkTimelineIndex]
            : TimeSpan.Zero;

        var positionDerivedIndex = MapTimelineIndexToOriginalChunkIndex(clip, badChunkTimelineIndex);

        if (_recoveryAttempts >= MaxRecoveryAttemptsPerClip)
        {
            GiveUpOnClip(clip, positionDerivedIndex);
            return;
        }

        _recoveryAttempts++;

        Log.Warning(
            "Premature end of playback detected; attempting corrupt-chunk recovery. ClipName={ClipName}; ClipPath={ClipPath}; FailurePosition={FailurePosition}; Duration={Duration}; Attempt={Attempt}",
            clip.Name,
            clip.FullPath,
            failurePosition,
            Duration,
            _recoveryAttempts);

        var requestId = BeginNewRequest();

        try
        {
            await RunSerializedPlaybackOperationAsync(async ct =>
            {
                if (!IsRequestActive(requestId) || clip != CurrentClip)
                    return;

                // Probe-first: rebuild with the current exclusion set only.
                // The builder re-probes every file, so a chunk that became unreadable since the last build shows up in AutoExcludedChunkIndices -- that's the real culprit, and the (possibly healthy) chunk under the failure position must NOT be excluded.
                var excludedSnapshot = SnapshotExcludedChunkIndices();
                var newMediaSource = await Task.Run(
                    () => _mediaSourceBuilder.Build(clip, excludedSnapshot),
                    ct);

                var probeFoundCulprits = newMediaSource.AutoExcludedChunkIndices
                    .Any(index => !excludedSnapshot.Contains(index));

                if (probeFoundCulprits)
                {
                    Log.Warning(
                        "Probe found unreadable chunk(s); excluding them instead of the failure-position chunk. ClipName={ClipName}; ClipPath={ClipPath}; AutoExcludedChunkIndices={AutoExcludedChunkIndices}",
                        clip.Name,
                        clip.FullPath,
                        newMediaSource.AutoExcludedChunkIndices);
                }
                else
                {
                    // Probe-clean corruption (moov intact, media data bad): fall back to excluding the chunk containing the failure position and rebuild again.
                    bool excludedNewChunk;
                    int excludedChunkCount;
                    lock (_excludedChunkIndicesLock)
                    {
                        excludedNewChunk = positionDerivedIndex >= 0 && _excludedChunkIndices.Add(positionDerivedIndex);
                        excludedChunkCount = _excludedChunkIndices.Count;
                    }

                    if (!excludedNewChunk || excludedChunkCount >= clip.Chunks.Count)
                    {
                        GiveUpOnClip(clip, positionDerivedIndex);
                        return;
                    }

                    Log.Warning(
                        "Probe found nothing new; excluding the chunk containing the failure position. ClipName={ClipName}; ClipPath={ClipPath}; BadChunkIndex={BadChunkIndex}; ChunkTimestamp={ChunkTimestamp}",
                        clip.Name,
                        clip.FullPath,
                        positionDerivedIndex,
                        clip.Chunks[positionDerivedIndex].Timestamp);

                    var rebuildSnapshot = SnapshotExcludedChunkIndices();
                    newMediaSource = await Task.Run(
                        () => _mediaSourceBuilder.Build(clip, rebuildSnapshot),
                        ct);
                }

                if (newMediaSource.ChunkStarts.Count == 0)
                {
                    GiveUpOnClip(clip, positionDerivedIndex);
                    return;
                }

                Duration = newMediaSource.Duration;

                await OpenClipInternalAsync(clip, newMediaSource, playAfterOpen: false, requestId, ct);

                if (!IsRequestActive(requestId) || clip != CurrentClip || !IsMediaOpen)
                    return;

                var clampedResumePosition = Clamp(resumePosition, TimeSpan.Zero, Duration);

                // Resume playback BEFORE seeking: a seek issued while paused right after open can be swallowed by the player, whereas seeks during active playback are reliable.
                if (wasPlaying)
                {
                    await PlayOpenPlayersAsync();
                    IsPlaying = true;
                }

                await SeekOpenPlayersAsync(clampedResumePosition);
                Position = clampedResumePosition;

                // One-shot guard against the reopen/seek race: give the player a moment and, if its reported position is still far below the resume target, reissue the seek.
                await _postRecoverySeekVerifyDelay(ct);

                if (IsRequestActive(requestId)
                    && clampedResumePosition - Position > PostRecoverySeekTolerance)
                {
                    Log.Warning(
                        "Post-recovery seek did not stick; reissuing. ClipName={ClipName}; ClipPath={ClipPath}; ResumePosition={ResumePosition}; ReportedPosition={ReportedPosition}",
                        clip.Name,
                        clip.FullPath,
                        clampedResumePosition,
                        Position);
                    await SeekOpenPlayersAsync(clampedResumePosition);
                    Position = clampedResumePosition;
                }
            }, replacePlaybackCts: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(
                ex,
                "Error recovering from premature end of playback. ClipName={ClipName}; ClipPath={ClipPath}; RequestId={RequestId}",
                clip.Name,
                clip.FullPath,
                requestId);
            ErrorMessage = $"Playback error: {ex.Message}";
        }
        finally
        {
            // The failure may have arrived while the original open was still joining secondary cameras (IsLoading true).
            // Recovery bumped the request id above, so that open's finally no longer owns the flag; settle it here or the loading state sticks forever.
            if (IsRequestActive(requestId))
            {
                IsLoading = false;
            }
        }
    }

    private void GiveUpOnClip(CamClip clip, int badChunkIndex)
    {
        Log.Error(
            "Giving up on clip playback after repeated unreadable chunks. ClipName={ClipName}; ClipPath={ClipPath}; BadChunkIndex={BadChunkIndex}; Attempts={Attempts}",
            clip.Name,
            clip.FullPath,
            badChunkIndex,
            _recoveryAttempts);
        ErrorMessage = EncryptedClipDetector.LooksEncrypted(clip)
            ? EncryptedClipMessage
            : "Playback stopped: too many unreadable video files.";
        IsMediaOpen = false;
    }

    /// <summary>
    /// Maps an index into the currently opened (possibly already-shrunk) timeline's <see cref="ClipMediaSource.ChunkStarts"/> back to the corresponding index in the original clip's <see cref="CamClip.Chunks"/>, accounting for chunks already excluded.
    /// </summary>
    private HashSet<int> SnapshotExcludedChunkIndices()
    {
        lock (_excludedChunkIndicesLock)
        {
            return new HashSet<int>(_excludedChunkIndices);
        }
    }

    private int MapTimelineIndexToOriginalChunkIndex(CamClip clip, int timelineIndex)
    {
        // Snapshot under the lock: this runs on a Flyleaf callback thread during recovery, outside the operation lock, while a concurrent clip change can clear the exclusion set.
        var excluded = SnapshotExcludedChunkIndices();
        var remaining = timelineIndex;

        for (var originalIndex = 0; originalIndex < clip.Chunks.Count; originalIndex++)
        {
            if (excluded.Contains(originalIndex))
            {
                continue;
            }

            if (remaining == 0)
            {
                return originalIndex;
            }

            remaining--;
        }

        return -1;
    }

    private async void OnPlayerFailed(object sender, CameraPlaybackFailedEventArgs e)
    {
        var camera = GetCameraName(sender);
        if (!ReferenceEquals(sender, _primaryPlayer))
        {
            Log.Warning(
                e.ErrorException,
                "Secondary camera playback failed. Camera={Camera}; ClipName={ClipName}; ClipPath={ClipPath}",
                camera,
                CurrentClip?.Name,
                CurrentClip?.FullPath);
            return;
        }

        // A chunk whose moov is intact but whose media data is truncated/corrupt makes Flyleaf raise Failed ("Playback stopped unexpectedly") rather than Ended when the concat demuxer dies mid-clip.
        // Route that into the same corrupt-chunk recovery as a premature Ended; only genuinely unrecoverable failures fall through to the error UI below.
        if (!_isDisposed && !_isOpeningMedia && IsMediaOpen && _openedMediaSource is not null
            && Duration - Position > PrematureEndTolerance)
        {
            // Same as OnPlayerEnded: not gated on IsLoading, so a front failure during the secondary-camera join window still reaches recovery instead of the error UI below.
            var mediaRequestId = Volatile.Read(ref _currentMediaRequestId);
            if (mediaRequestId != 0 && mediaRequestId == Volatile.Read(ref _activeRequestId))
            {
                Log.Warning(
                    e.ErrorException,
                    "Front camera playback failed mid-clip; attempting corrupt-chunk recovery. ClipName={ClipName}; ClipPath={ClipPath}; Position={Position}; Duration={Duration}",
                    CurrentClip?.Name,
                    CurrentClip?.FullPath,
                    Position,
                    Duration);

                var wasPlaying = IsPlaying;
                IsPlaying = false;
                await RecoverFromPrematureEndAsync(wasPlaying);
                return;
            }
        }

        Log.Error(
            e.ErrorException,
            "Media playback failed. Camera={Camera}; ClipName={ClipName}; ClipPath={ClipPath}",
            camera,
            CurrentClip?.Name,
            CurrentClip?.FullPath);
        ErrorMessage = $"Playback failed: {e.ErrorException?.Message}";
        IsPlaying = false;
        IsLoading = false;
        IsMediaOpen = false;
    }

    private void OnPositionChanged(object sender, CameraPositionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _primaryPlayer) || _isOpeningMedia)
            return;

        Position = e.Position;
    }

    private string GetCameraName(object sender)
    {
        foreach (var (camera, player) in _players)
        {
            if (ReferenceEquals(sender, player))
            {
                return camera;
            }
        }

        return "unknown";
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min)
            return min;

        if (value > max)
            return max;

        return value;
    }
}
