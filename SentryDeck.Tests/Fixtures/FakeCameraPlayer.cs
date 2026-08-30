namespace SentryDeck.Tests;

/// <summary>
/// In-memory <see cref="ICameraPlayer"/> used to drive a real <see cref="VideoPlayerController"/> in tests without Flyleaf/FFmpeg.
/// </summary>
internal sealed class FakeCameraPlayer : ICameraPlayer
{
    public event EventHandler Opened;
    public event EventHandler Ended;
    public event EventHandler<CameraPlaybackFailedEventArgs> Failed;
    public event EventHandler<CameraPositionChangedEventArgs> PositionChanged;

    public List<string> OpenedPaths { get; } = [];
    public List<TimeSpan> SeekPositions { get; } = [];

    /// <summary>Ordered log of the <c>accurate</c> flag passed to each <see cref="SeekAsync"/> call.</summary>
    public List<bool> SeekAccurateFlags { get; } = [];

    /// <summary>
    /// Ordered log of play/pause/seek calls so tests can assert on call ordering (e.g. that a post-recovery resume plays before it seeks).
    /// </summary>
    public List<string> CallLog { get; } = [];
    public bool OpenResult { get; init; } = true;
    public bool ThrowOnStop { get; init; }
    public TaskCompletionSource<object> StopGate { get; set; }
    public bool IsOpen { get; private set; }
    public double Speed { get; set; } = 1.0;

    /// <summary>
    /// Test-controlled position, used by the controller to read a camera's "live" position (e.g. the front player's current time when joining a secondary camera mid-playback).
    /// Defaults to zero and is kept in sync by <see cref="SeekAsync"/> so tests behave sensibly without having to poke it manually after every seek.
    /// </summary>
    public TimeSpan Position { get; set; }

    /// <summary>
    /// Optional gate that, when set, makes <see cref="OpenAsync"/> await it before completing -- lets tests hold a camera's open in progress to assert on ordering/timing.
    /// </summary>
    public TaskCompletionSource<object> OpenGate { get; set; }

    /// <summary>
    /// Optional hook invoked synchronously inside <see cref="SeekAsync"/> (after recording the call, before updating <see cref="Position"/>) -- lets tests interleave actions mid-seek (e.g. a new drag gesture starting while the previous release's accurate seek is still executing) without leaving the test thread.
    /// </summary>
    public Action SeekCallback { get; set; }

    public int PlayCount { get; private set; }
    public int PauseCount { get; private set; }
    public int StopCount { get; private set; }
    public int CloseCount { get; private set; }
    public int DisposeCount { get; private set; }

    public async Task<bool> OpenAsync(string path)
    {
        OpenedPaths.Add(path);

        if (OpenGate is not null)
        {
            await OpenGate.Task;
        }

        IsOpen = OpenResult;

        if (OpenResult)
        {
            Opened?.Invoke(this, EventArgs.Empty);
        }

        return OpenResult;
    }

    /// <summary>
    /// Optional hook invoked synchronously inside <see cref="PlayAsync"/>, mirroring <see cref="SeekCallback"/>.
    /// Lets a test make the clip end while a play operation is still in flight, which is the ordering that used to leave the controller reporting playback on a finished clip.
    /// </summary>
    public Action PlayCallback { get; set; }

    public Task PlayAsync()
    {
        PlayCount++;
        CallLog.Add("play");
        PlayCallback?.Invoke();
        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        PauseCount++;
        CallLog.Add("pause");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        StopCount++;
        if (StopGate is not null)
        {
            return StopGate.Task;
        }

        return ThrowOnStop
            ? Task.FromException(new InvalidOperationException("stop failed"))
            : Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        CloseCount++;
        IsOpen = false;
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position, bool accurate = true)
    {
        SeekPositions.Add(position);
        SeekAccurateFlags.Add(accurate);
        CallLog.Add(accurate ? $"seek:{position.TotalSeconds}" : $"scrub:{position.TotalSeconds}");
        SeekCallback?.Invoke();
        Position = position;
        PositionChanged?.Invoke(this, new CameraPositionChangedEventArgs(position));
        return Task.CompletedTask;
    }

    /// <summary>Ordered log of <see cref="StepFrameAsync"/> calls: <c>"forward"</c> or <c>"backward"</c>.</summary>
    public List<string> StepLog { get; } = [];

    public Task StepFrameAsync(bool forward)
    {
        StepLog.Add(forward ? "forward" : "backward");
        CallLog.Add(forward ? "step:forward" : "step:backward");
        return Task.CompletedTask;
    }

    public void RaiseEnded()
    {
        Ended?.Invoke(this, EventArgs.Empty);
    }

    public void RaiseFailed(Exception exception)
    {
        Failed?.Invoke(this, new CameraPlaybackFailedEventArgs(exception));
    }

    public void RaisePositionChanged(TimeSpan position)
    {
        Position = position;
        PositionChanged?.Invoke(this, new CameraPositionChangedEventArgs(position));
    }

    public void Dispose()
    {
        DisposeCount++;
    }
}
