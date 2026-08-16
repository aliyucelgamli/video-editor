using VideoEditor.Application.Commands;
using VideoEditor.Domain;

namespace VideoEditor.Tests;

/// <summary>
/// "Fit to the clips" backs both double-click gestures on the ruler, so what it
/// reports about where the content begins and ends has to be exact.
/// </summary>
public static class TimelineNavigationTests
{
    public static void Register()
    {
        TestRunner.Add("Extent: spans the earliest start to the latest end", () =>
        {
            var project = new Project();
            var video = new Track { Name = "V1", Type = TrackType.Video };
            var overlay = new Track { Name = "T1", Type = TrackType.Overlay };
            project.Tracks.Add(video);
            project.Tracks.Add(overlay);

            Assert.True(project.ContentExtent() is null, "an empty timeline has no extent");

            // Content does not start at zero — that is the whole point of the extent.
            video.Events.Add(new TimelineEvent { Name = "a", Start = 4, Duration = 3 });
            overlay.Events.Add(new TimelineEvent { Name = "title", Start = 2.5, Duration = 1 });
            video.Events.Add(new TimelineEvent { Name = "b", Start = 9, Duration = 2.5 });

            var extent = project.ContentExtent();
            Assert.True(extent != null, "extent found");
            Assert.Close(2.5, extent!.Start, "starts at the earliest clip, on any track", 0.0001);
            Assert.Close(11.5, extent.End, "ends at the latest clip", 0.0001);
            Assert.Close(project.Duration, extent.End, "the end agrees with Duration", 0.0001);

            // A zero-length timeline is not something to zoom or select into.
            var empty = new Project();
            var lane = new Track { Name = "V1", Type = TrackType.Video };
            lane.Events.Add(new TimelineEvent { Start = 3, Duration = 0 });
            empty.Tracks.Add(lane);
            Assert.True(empty.ContentExtent() is null, "a zero-length span is no extent");
        });

        TestRunner.Add("Tracks: deleting one takes its clips and undo brings them back", () =>
        {
            var project = new Project();
            var keep = new Track { Name = "V1", Type = TrackType.Video };
            var doomed = new Track { Name = "T1", Type = TrackType.Overlay };
            var audio = new Track { Name = "A1", Type = TrackType.Audio };
            project.Tracks.Add(keep);
            project.Tracks.Add(doomed);
            project.Tracks.Add(audio);
            doomed.Events.Add(new TimelineEvent { Name = "title", Start = 1, Duration = 2 });
            doomed.Events.Add(new TimelineEvent { Name = "logo", Start = 4, Duration = 2 });

            var remove = new RemoveTrackCommand(project, doomed);
            Assert.Equal(2, remove.EventCount, "the warning knows how many clips are at stake");

            remove.Execute();
            Assert.Equal(2, project.Tracks.Count, "the lane is gone");
            Assert.True(project.ContentExtent() is null, "and so are its clips");

            remove.Undo();
            Assert.Equal(3, project.Tracks.Count, "undo restores the lane");
            Assert.Equal("T1", project.Tracks[1].Name, "in its original position");
            Assert.Equal(2, project.Tracks[1].Events.Count, "with its clips intact");
        });

        TestRunner.Add("Tracks: an empty lane deletes without losing anything", () =>
        {
            var project = new Project();
            var lane = new Track { Name = "V2", Type = TrackType.Video };
            project.Tracks.Add(new Track { Name = "V1", Type = TrackType.Video });
            project.Tracks.Add(lane);

            var remove = new RemoveTrackCommand(project, lane);
            Assert.Equal(0, remove.EventCount, "nothing on it, so nothing to warn about");
            remove.Execute();
            Assert.Equal(1, project.Tracks.Count, "removed");
            remove.Undo();
            Assert.Equal(2, project.Tracks.Count, "and restored");
        });
    }
}
