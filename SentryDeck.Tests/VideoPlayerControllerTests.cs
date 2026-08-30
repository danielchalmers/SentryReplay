using System.IO;

namespace SentryDeck.Tests;

/// <summary>
/// Split across VideoPlayerControllerTests.*.cs by feature.
/// This file holds the harness plus opening, transport and playlist navigation; recovery and camera-join behaviour live in the Recovery partial.
/// </summary>
public sealed partial class VideoPlayerControllerTests
{
    // Clones a clip with an event at the given wall-clock instant (TestClipFiles builds event-less clips), preserving its real chunk files so the media source builds from the same footage.
    private static CamClip WithEvent(CamClip clip, DateTime eventTimestamp) =>
        new(clip.FullPath, clip.Name, clip.Timestamp, clip.Chunks, new CamEvent { Timestamp = eventTimestamp });

    private static VideoPlayerController CreateController(
        FakeCameraPlayer front = null,
        FakeCameraPlayer back = null,
        FakeCameraPlayer left = null,
        FakeCameraPlayer right = null,
        IClipMediaSourceBuilder mediaSourceBuilder = null,
        Func<CancellationToken, Task> postRecoverySeekVerifyDelay = null)
    {
        var players = new Dictionary<string, ICameraPlayer>
        {
            [CameraNames.Front] = front ?? new FakeCameraPlayer(),
            [CameraNames.Back] = back ?? new FakeCameraPlayer(),
            [CameraNames.LeftRepeater] = left ?? new FakeCameraPlayer(),
            [CameraNames.RightRepeater] = right ?? new FakeCameraPlayer(),
        };

        return new VideoPlayerController(
            players,
            CameraNames.Front,
            mediaSourceBuilder ?? new FakeClipMediaSourceBuilder(),
            // FakeCameraPlayer reports its position the moment a seek is applied, so the real verify wait buys nothing here and every recovery test would otherwise pay it in wall-clock time.
            postRecoverySeekVerifyDelay ?? (_ => Task.CompletedTask));
    }

    /// <summary>
    /// Waits until a clip is fully opened: playback has started AND the open operation has completed (IsLoading cleared).
    /// Events raised between Play and the end of the open operation are dropped by the controller's stale-event guards, so tests that raise front-player events must wait for this state, not just PlayCount.
    /// </summary>
    private static Task WaitUntilClipOpenedAsync(VideoPlayerController controller, FakeCameraPlayer front)
    {
        return Wait.UntilAsync(() => front.PlayCount > 0 && controller.IsMediaOpen && !controller.IsLoading);
    }

    [Fact]
    public async Task SelectingClip_OpensAndPlaysAllAvailableCameras()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        var back = new FakeCameraPlayer();
        var left = new FakeCameraPlayer();
        var right = new FakeCameraPlayer();
        using var controller = CreateController(front, back, left, right);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);

        await Wait.UntilAsync(() =>
            front.PlayCount > 0 &&
            back.PlayCount > 0 &&
            left.PlayCount > 0 &&
            right.PlayCount > 0);

        front.OpenedPaths.ShouldContain(path => path.EndsWith(".ffconcat", StringComparison.OrdinalIgnoreCase) && path.Contains("-front.mp4"));
        back.OpenedPaths.ShouldContain(path => path.EndsWith(".ffconcat", StringComparison.OrdinalIgnoreCase) && path.Contains("-back.mp4"));
        left.OpenedPaths.ShouldContain(path => path.EndsWith(".ffconcat", StringComparison.OrdinalIgnoreCase) && path.Contains("-left_repeater.mp4"));
        right.OpenedPaths.ShouldContain(path => path.EndsWith(".ffconcat", StringComparison.OrdinalIgnoreCase) && path.Contains("-right_repeater.mp4"));
        controller.IsPlaying.ShouldBeTrue();
        controller.IsMediaOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task SelectingClip_WithPillarCameras_OpensAndPlaysEveryCamera()
    {
        // An HW4 clip carries six cameras; the camera-keyed pool must open and play all of them, not just the classic four.
        using var clipFiles = TestClipFiles.Create(chunkCount: 1); // default fixture = all six cameras
        var players = CameraNames.All.ToDictionary(camera => camera, _ => new FakeCameraPlayer());
        using var controller = new VideoPlayerController(
            players.ToDictionary(pair => pair.Key, pair => (ICameraPlayer)pair.Value),
            CameraNames.Front,
            new FakeClipMediaSourceBuilder());

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);

        await Wait.UntilAsync(() => players.Values.All(player => player.PlayCount > 0));

        players[CameraNames.LeftPillar].OpenedPaths.ShouldContain(path => path.Contains("-left_pillar.mp4"));
        players[CameraNames.RightPillar].OpenedPaths.ShouldContain(path => path.Contains("-right_pillar.mp4"));
    }

    [Fact]
    public async Task SelectingClip_WhenSecondaryFileMissing_PlaysRemainingCameras()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1, omitCamerasFromChunkZero: new HashSet<string> { CameraNames.LeftRepeater });
        var front = new FakeCameraPlayer();
        var back = new FakeCameraPlayer();
        var left = new FakeCameraPlayer();
        var right = new FakeCameraPlayer();
        using var controller = CreateController(front, back, left, right);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);

        await Wait.UntilAsync(() =>
            front.PlayCount > 0 &&
            back.PlayCount > 0 &&
            right.PlayCount > 0);

        left.OpenedPaths.ShouldBeEmpty();
        left.PlayCount.ShouldBe(0);
        controller.ErrorMessage.ShouldBeNull();
        controller.IsPlaying.ShouldBeTrue();
        controller.IsMediaOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task SelectingClip_WhenFrontFileMissing_ReportsOpenFailure()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1, omitCamerasFromChunkZero: new HashSet<string> { CameraNames.Front });
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);

        await Wait.UntilAsync(() => controller.ErrorMessage is not null);

        controller.ErrorMessage.ShouldBe("No front camera footage found.");
        controller.IsPlaying.ShouldBeFalse();
        front.OpenedPaths.ShouldBeEmpty();
    }

    [Fact]
    public async Task SelectingClip_WhenAllFilesAreEncrypted_ExplainsTheEncryptionToggle()
    {
        // A drive written by Tesla software 2026.20+ with "Encrypt Dashcam Recordings" on: every file exists but none is a playable MP4. The real builder probes and excludes every chunk, and the error must point at the encryption toggle, not claim missing footage.
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        foreach (var chunk in clipFiles.Clip.Chunks)
        {
            foreach (var file in chunk.Files.Values)
            {
                File.WriteAllBytes(file.FullPath, TestMp4.EncryptedLookingBytes);
            }
        }

        var front = new FakeCameraPlayer();
        using var playlists = new TestPlaylistDirectory();
        using var controller = CreateController(front, mediaSourceBuilder: playlists.CreateBuilder());

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);

        await Wait.UntilAsync(() => controller.ErrorMessage is not null);

        controller.ErrorMessage.ShouldBe(VideoPlayerController.EncryptedClipMessage);
        controller.ErrorMessage.ShouldContain("Encrypt Dashcam Recordings");
        controller.IsPlaying.ShouldBeFalse();
        front.OpenedPaths.ShouldBeEmpty();
    }

    [Fact]
    public async Task SelectingClip_WhenAllFilesAreGarbage_ButNotEncrypted_KeepsTheCorruptMessage()
    {
        // Same all-unreadable shape, but the files still carry MP4 headers (truncated writes): that's ordinary corruption and must NOT be blamed on encryption.
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var truncated = TestMp4.BuildWithDuration(TimeSpan.FromSeconds(60))[..12];
        foreach (var file in clipFiles.Clip.Chunks[0].Files.Values)
        {
            File.WriteAllBytes(file.FullPath, truncated);
        }

        var front = new FakeCameraPlayer();
        using var playlists = new TestPlaylistDirectory();
        using var controller = CreateController(front, mediaSourceBuilder: playlists.CreateBuilder());

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);

        await Wait.UntilAsync(() => controller.ErrorMessage is not null);

        controller.ErrorMessage.ShouldBe("No front camera footage found.");
        controller.IsPlaying.ShouldBeFalse();
    }

    [Fact]
    public async Task PrimaryCameraFailsToOpen_ReportsFailureWithoutPlaying()
    {
        // The playlist exists and is handed to the player, but the player itself refuses it (a codec/handle failure inside Flyleaf).
        // Unlike the missing-footage cases above, the open WAS attempted -- and nothing past it may happen: no play, and no secondary cameras.
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer { OpenResult = false };
        var back = new FakeCameraPlayer();
        using var controller = CreateController(front, back);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);

        await Wait.UntilAsync(() => controller.ErrorMessage is not null);

        controller.ErrorMessage.ShouldBe("Failed to open front camera video.");
        front.OpenedPaths.Count.ShouldBe(1);
        front.PlayCount.ShouldBe(0);
        back.OpenedPaths.ShouldBeEmpty();
        controller.IsPlaying.ShouldBeFalse();
        controller.IsMediaOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task PauseSeekAndStop_ControlOpenPlayers()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        var back = new FakeCameraPlayer();
        using var controller = CreateController(front, back);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await Wait.UntilAsync(() => front.PlayCount > 0 && back.PlayCount > 0);

        await controller.PauseAsync();
        await controller.SeekAsync(TimeSpan.FromSeconds(12));
        await controller.StopAsync();

        front.PauseCount.ShouldBe(1);
        back.PauseCount.ShouldBe(1);
        front.SeekPositions.ShouldContain(TimeSpan.FromSeconds(12));
        back.SeekPositions.ShouldContain(TimeSpan.FromSeconds(12));
        controller.Position.ShouldBe(TimeSpan.Zero);
        controller.Duration.ShouldBe(TimeSpan.Zero);
        controller.IsPlaying.ShouldBeFalse();
        controller.IsMediaOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task PlayAsync_OnTheAlreadyOpenClip_ResumesWithoutRebuilding()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var front = new FakeCameraPlayer();
        var mediaSourceBuilder = new FakeClipMediaSourceBuilder();
        using var controller = CreateController(front, mediaSourceBuilder: mediaSourceBuilder);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        var openCountBeforeResume = front.OpenedPaths.Count;
        await controller.PauseAsync();

        await controller.PlayAsync();

        // Resuming the clip that's already open must take the resume fast path: no rebuild, no reopen, just play.
        // Rebuilding here would restart the clip from scratch on every pause.
        mediaSourceBuilder.BuildCount.ShouldBe(1);
        front.OpenedPaths.Count.ShouldBe(openCountBeforeResume);
        front.PlayCount.ShouldBe(2);
        controller.IsPlaying.ShouldBeTrue();
    }

    [Fact]
    public async Task PlayAsync_AtEndOfClip_RestartsFromZero()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        // Playback parks at the end of a finished clip rather than advancing, so pressing play there has to mean "replay" -- otherwise the button does nothing at all.
        front.RaisePositionChanged(controller.Duration);

        await controller.PlayAsync();

        front.SeekPositions.ShouldContain(TimeSpan.Zero);
        controller.Position.ShouldBe(TimeSpan.Zero);
        controller.IsPlaying.ShouldBeTrue();
    }

    [Fact]
    public async Task ScrubSeekAsync_IssuesFastSeeksToOpenPlayers()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        var back = new FakeCameraPlayer();
        using var controller = CreateController(front, back);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await Wait.UntilAsync(() => front.PlayCount > 0 && back.PlayCount > 0);

        await controller.ScrubSeekAsync(TimeSpan.FromSeconds(12));

        front.SeekPositions.ShouldContain(TimeSpan.FromSeconds(12));
        back.SeekPositions.ShouldContain(TimeSpan.FromSeconds(12));
        front.SeekAccurateFlags[^1].ShouldBeFalse();
        back.SeekAccurateFlags[^1].ShouldBeFalse();
        controller.Position.ShouldBe(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public async Task SeekAsync_IssuesAccurateSeeksToOpenPlayers()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await Wait.UntilAsync(() => front.PlayCount > 0);

        await controller.SeekAsync(TimeSpan.FromSeconds(12));

        front.SeekAccurateFlags[^1].ShouldBeTrue();
    }

    [Fact]
    public async Task StopAsync_ClosesPlayersEvenWhenStopFails()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer { ThrowOnStop = true };
        var back = new FakeCameraPlayer();
        using var controller = CreateController(front, back);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await Wait.UntilAsync(() => front.PlayCount > 0);

        await controller.StopAsync();

        front.StopCount.ShouldBeGreaterThan(0);
        front.CloseCount.ShouldBeGreaterThan(0);
        back.CloseCount.ShouldBeGreaterThan(0);
        controller.IsMediaOpen.ShouldBeFalse();
        controller.IsPlaying.ShouldBeFalse();
    }

    [Fact]
    public async Task FrontMediaFailed_NearEndOfClip_ReportsPlaybackFailure()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        // A failure within the premature-end tolerance of Duration is not a corrupt-chunk candidate, so it must surface as a plain playback error.
        front.RaisePositionChanged(controller.Duration - TimeSpan.FromSeconds(1));
        front.RaiseFailed(new InvalidOperationException("decode failed"));

        controller.ErrorMessage.ShouldContain("decode failed");
        controller.IsPlaying.ShouldBeFalse();
        controller.IsMediaOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task SecondaryCameraFailure_DoesNotStopPrimaryPlayback()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        var back = new FakeCameraPlayer();
        using var controller = CreateController(front, back);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await Wait.UntilAsync(() => front.PlayCount > 0 && back.PlayCount > 0);

        back.RaiseFailed(new InvalidOperationException("secondary failed"));

        controller.ErrorMessage.ShouldBeNull();
        controller.IsPlaying.ShouldBeTrue();
        controller.IsMediaOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task SecondaryCameraEnded_DoesNotStopOrRecover()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 3);
        var front = new FakeCameraPlayer();
        var back = new FakeCameraPlayer();
        var mediaSourceBuilder = new FakeClipMediaSourceBuilder();
        using var controller = CreateController(front, back, mediaSourceBuilder: mediaSourceBuilder);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        var buildCountBeforeEnded = mediaSourceBuilder.BuildCount;
        var positionBeforeEnded = controller.Position;

        // A secondary camera with fewer usable chunks runs out of footage long before the front does.
        // Only the primary drives the timeline, so this must neither park playback at the end nor start corrupt-chunk recovery -- the front is still mid-clip.
        back.RaiseEnded();

        controller.IsPlaying.ShouldBeTrue();
        controller.Position.ShouldBe(positionBeforeEnded);
        mediaSourceBuilder.BuildCount.ShouldBe(buildCountBeforeEnded);
        controller.ErrorMessage.ShouldBeNull();
        controller.IsMediaOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task FrontMediaEnded_WithinTolerance_CompletesNormallyWithoutRebuilding()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var front = new FakeCameraPlayer();
        var mediaSourceBuilder = new FakeClipMediaSourceBuilder();
        using var controller = CreateController(front, mediaSourceBuilder: mediaSourceBuilder);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        var openCountBeforeEnded = front.OpenedPaths.Count;
        var duration = controller.Duration;

        // A genuine end-of-clip: position reaches Duration before Ended fires.
        front.RaisePositionChanged(duration);
        front.RaiseEnded();

        await Wait.UntilAsync(() => controller.Position == duration && !controller.IsPlaying);

        // The whole clip is one playlist per camera opened once; hitting the end of the playlist must not trigger another OpenAsync call (that would be the old per-chunk stall).
        front.OpenedPaths.Count.ShouldBe(openCountBeforeEnded);
        mediaSourceBuilder.BuildCount.ShouldBe(1);
        controller.Position.ShouldBe(duration);
        controller.IsPlaying.ShouldBeFalse();
        // The media stays open at the end so the scrubber and frame-step remain usable.
        controller.IsMediaOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task PlayAsync_WhenTheClipEndsDuringTheCall_DoesNotReportPlaying()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        await controller.PauseAsync();
        var duration = controller.Duration;

        // Play pressed near the end, or queued behind a slow open: the clip runs out while the play operation is still in flight.
        // The Ended handler clears IsPlaying, and the play call must not then set it back, or the transport claims to be playing a clip parked on its last frame until the user presses something else.
        front.PlayCallback = () =>
        {
            front.PlayCallback = null;
            front.RaisePositionChanged(duration);
            front.RaiseEnded();
        };

        await controller.PlayAsync();

        await Wait.UntilAsync(() => controller.Position == duration);
        controller.IsPlaying.ShouldBeFalse();
        controller.Position.ShouldBe(duration);
        // The clip is finished, not broken: the media stays open so the scrubber and frame-step still work.
        controller.IsMediaOpen.ShouldBeTrue();
        controller.ErrorMessage.ShouldBeNull();
    }

    [Fact]
    public async Task PlayAsync_WhenTheClipKeepsPlaying_ReportsPlaying()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        await controller.PauseAsync();
        controller.IsPlaying.ShouldBeFalse();

        // The guard above must only suppress the state a finished clip already settled; an ordinary resume still reports playback.
        await controller.PlayAsync();

        controller.IsPlaying.ShouldBeTrue();
    }

    [Fact]
    public async Task FrontMediaEnded_WithNextClip_StaysOnCurrentClipWithoutAdvancing()
    {
        using var firstClipFiles = TestClipFiles.Create(chunkCount: 2);
        using var secondClipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        var mediaSourceBuilder = new FakeClipMediaSourceBuilder();
        using var controller = CreateController(front, mediaSourceBuilder: mediaSourceBuilder);

        controller.LoadClips([firstClipFiles.Clip, secondClipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        var duration = controller.Duration;

        // A genuine end-of-clip: position reaches (within tolerance of) Duration before Ended fires.
        front.RaisePositionChanged(duration);
        front.RaiseEnded();

        await Wait.UntilAsync(() => controller.Position == duration && !controller.IsPlaying);

        // No auto-advance: the user stays on the finished clip (most likely to replay it), and the next clip is never opened or built.
        // Next remains an explicit action.
        controller.CurrentClip.ShouldBe(firstClipFiles.Clip);
        mediaSourceBuilder.BuildCountFor(secondClipFiles.Clip).ShouldBe(0);
        controller.Position.ShouldBe(duration);
        controller.IsPlaying.ShouldBeFalse();
        controller.CanGoNext.ShouldBeTrue();
        // The media stays open at the end so the scrubber and frame-step remain usable.
        controller.IsMediaOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task SeekAsync_PastOldChunkBoundary_SeeksOpenPlayersWithoutReopening()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var front = new FakeCameraPlayer();
        var mediaSourceBuilder = new FakeClipMediaSourceBuilder();
        using var controller = CreateController(front, mediaSourceBuilder: mediaSourceBuilder);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await Wait.UntilAsync(() => front.PlayCount > 0);

        var openCountBeforeSeek = front.OpenedPaths.Count;

        // 75s is past the old 60s per-chunk boundary; the clip is now a single continuous playlist, so this must be a plain seek with no reopen.
        await controller.SeekAsync(TimeSpan.FromSeconds(75));

        front.OpenedPaths.Count.ShouldBe(openCountBeforeSeek);
        mediaSourceBuilder.BuildCount.ShouldBe(1);
        front.SeekPositions.ShouldContain(TimeSpan.FromSeconds(75));
        controller.Position.ShouldBe(TimeSpan.FromSeconds(75));
        controller.IsPlaying.ShouldBeTrue();
    }

    [Fact]
    public async Task SeekAsync_BeyondDuration_ClampsToDuration()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await Wait.UntilAsync(() => front.PlayCount > 0);

        await controller.SeekAsync(TimeSpan.FromSeconds(999));

        controller.Position.ShouldBe(controller.Duration);
        front.SeekPositions.ShouldContain(controller.Duration);
    }

    [Fact]
    public async Task PositionChanged_ReportsFrontPlayerPositionDirectly()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 2);
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await Wait.UntilAsync(() => front.PlayCount > 0);

        front.RaisePositionChanged(TimeSpan.FromSeconds(68));

        controller.Position.ShouldBe(TimeSpan.FromSeconds(68));
    }

    [Fact]
    public async Task PlaybackSpeed_AppliesToExistingAndFuturePlayers()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        var back = new FakeCameraPlayer();
        using var controller = CreateController(front, back);

        controller.PlaybackSpeed = 2.0;
        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);

        await Wait.UntilAsync(() => front.PlayCount > 0 && back.PlayCount > 0);

        front.Speed.ShouldBe(2.0);
        back.Speed.ShouldBe(2.0);

        controller.PlaybackSpeed = 0;

        controller.PlaybackSpeed.ShouldBe(1.0);
        front.Speed.ShouldBe(1.0);
        back.Speed.ShouldBe(1.0);
    }

    [Fact]
    public async Task GoToClipAsync_ShowsLoadingWhileCurrentClipStops()
    {
        using var firstClipFiles = TestClipFiles.Create(chunkCount: 1);
        using var secondClipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([firstClipFiles.Clip, secondClipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await Wait.UntilAsync(() => front.PlayCount > 0);

        front.StopGate = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        var changeClipTask = controller.GoToClipAsync(secondClipFiles.Clip);

        await Wait.UntilAsync(() => front.StopCount > 0);

        controller.IsLoading.ShouldBeTrue();

        front.StopGate.SetResult(null);
        await changeClipTask;
        await Wait.UntilAsync(() => controller.CurrentClip == secondClipFiles.Clip && !controller.IsLoading);

        controller.CurrentClip.ShouldBe(secondClipFiles.Clip);
        controller.IsLoading.ShouldBeFalse();
    }

    [Fact]
    public async Task NextAsync_MovesToTheNextClipAndStopsTheCurrentOne()
    {
        using var firstClipFiles = TestClipFiles.Create(chunkCount: 1);
        using var secondClipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([firstClipFiles.Clip, secondClipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        var stopCountBeforeNext = front.StopCount;

        await controller.NextAsync();
        await WaitUntilClipOpenedAsync(controller, front);

        // The outgoing clip is torn down before the playlist moves, so the new clip never opens on top of players still holding the old one's playlist.
        controller.CurrentClip.ShouldBe(secondClipFiles.Clip);
        front.StopCount.ShouldBeGreaterThan(stopCountBeforeNext);
    }

    [Fact]
    public async Task PreviousAsync_MovesToThePreviousClip()
    {
        using var firstClipFiles = TestClipFiles.Create(chunkCount: 1);
        using var secondClipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([firstClipFiles.Clip, secondClipFiles.Clip]);
        controller.Playlist.MoveTo(1);
        await WaitUntilClipOpenedAsync(controller, front);

        var stopCountBeforePrevious = front.StopCount;

        await controller.PreviousAsync();
        await WaitUntilClipOpenedAsync(controller, front);

        controller.CurrentClip.ShouldBe(firstClipFiles.Clip);
        front.StopCount.ShouldBeGreaterThan(stopCountBeforePrevious);
    }

    [Fact]
    public async Task NextAsync_AtTheEndOfThePlaylist_IsANoOp()
    {
        using var clipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        var mediaSourceBuilder = new FakeClipMediaSourceBuilder();
        using var controller = CreateController(front, mediaSourceBuilder: mediaSourceBuilder);

        controller.LoadClips([clipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        // The only clip is also the last one.
        // Next must bail out before the teardown, not stop what's playing to then go nowhere.
        await controller.NextAsync();

        controller.CanGoNext.ShouldBeFalse();
        controller.CurrentClip.ShouldBe(clipFiles.Clip);
        mediaSourceBuilder.BuildCount.ShouldBe(1);
        controller.IsMediaOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task GoToClipAsync_ByIndex_MovesAndIgnoresOutOfRangeIndices()
    {
        using var firstClipFiles = TestClipFiles.Create(chunkCount: 1);
        using var secondClipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([firstClipFiles.Clip, secondClipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await WaitUntilClipOpenedAsync(controller, front);

        await controller.GoToClipAsync(1);
        await WaitUntilClipOpenedAsync(controller, front);

        controller.CurrentClip.ShouldBe(secondClipFiles.Clip);

        // An index that no longer addresses a clip (a stale selection from a list that has since shrunk) must leave playback exactly where it is.
        // CurrentClip alone doesn't prove that: ClipPlaylist.MoveTo rejects the bad index on its own, so the controller could still have torn playback down on the way there.
        // The stop count and the open/loading flags are what pin the controller's own guard.
        var stopCountBeforeBadIndex = front.StopCount;

        await controller.GoToClipAsync(-1);
        controller.CurrentClip.ShouldBe(secondClipFiles.Clip);
        front.StopCount.ShouldBe(stopCountBeforeBadIndex);
        controller.IsMediaOpen.ShouldBeTrue();
        controller.IsLoading.ShouldBeFalse();

        await controller.GoToClipAsync(99);
        controller.CurrentClip.ShouldBe(secondClipFiles.Clip);
        front.StopCount.ShouldBe(stopCountBeforeBadIndex);
        controller.IsMediaOpen.ShouldBeTrue();
        controller.IsLoading.ShouldBeFalse();
    }

    [Fact]
    public async Task Dispose_WhileOperationInFlight_DoesNotThrow()
    {
        using var firstClipFiles = TestClipFiles.Create(chunkCount: 1);
        using var secondClipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([firstClipFiles.Clip, secondClipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await Wait.UntilAsync(() => front.PlayCount > 0);

        // Hold the clip-change operation in flight -- it stops the current clip inside the serialized operation lock, so the lock is held while we dispose.
        front.StopGate = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var changeClipTask = controller.GoToClipAsync(secondClipFiles.Clip);
        await Wait.UntilAsync(() => front.StopCount > 0);

        // Closing the window disposes the controller (and its operation lock) mid-operation.
        controller.Dispose();

        // Let the in-flight operation finish.
        // Before the fix, releasing the now-disposed operation lock threw ObjectDisposedException, which surfaced through the awaited task.
        front.StopGate.SetResult(null);
        await changeClipTask;

        front.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task LoadClipsAsync_StopsCurrentPlaybackAndResetsSelection()
    {
        using var firstClipFiles = TestClipFiles.Create(chunkCount: 1);
        using var secondClipFiles = TestClipFiles.Create(chunkCount: 1);
        var front = new FakeCameraPlayer();
        using var controller = CreateController(front);

        controller.LoadClips([firstClipFiles.Clip]);
        controller.Playlist.MoveTo(0);
        await Wait.UntilAsync(() => front.PlayCount > 0);

        await controller.LoadClipsAsync([secondClipFiles.Clip]);

        controller.CurrentClip.ShouldBeNull();
        controller.Playlist.Clips.ShouldBe([secondClipFiles.Clip]);
        controller.IsPlaying.ShouldBeFalse();
        controller.Duration.ShouldBe(TimeSpan.Zero);
        front.CloseCount.ShouldBeGreaterThan(0);
    }
}
