using VideoEditor.Application.Commands;
using VideoEditor.Domain;
using VideoEditor.MediaEngine.Frames;

namespace VideoEditor.Tests;

/// <summary>Reordering lanes and what that means for the compositing stack.</summary>
public static class TrackOrderTests
{
    public static void Register()
    {
        TestRunner.Add("Tracks: move reorders lanes and undoes exactly", () =>
        {
            var project = new Project();
            var v1 = new Track { Name = "V1", Type = TrackType.Video };
            var a1 = new Track { Name = "A1", Type = TrackType.Audio };
            var t1 = new Track { Name = "T1", Type = TrackType.Overlay };
            project.Tracks.Add(v1);
            project.Tracks.Add(a1);
            project.Tracks.Add(t1);

            var move = new MoveTrackCommand(project, t1, 0);
            Assert.True(move.ChangesOrder, "moving to a new index changes order");
            move.Execute();
            Assert.Equal("T1", project.Tracks[0].Name, "overlay moved to the top lane");
            Assert.Equal("V1", project.Tracks[1].Name, "video shifted down");

            move.Undo();
            Assert.Equal("V1", project.Tracks[0].Name, "undo restores the order");
            Assert.Equal("T1", project.Tracks[2].Name, "overlay is back at the bottom");

            // Dropping a lane where it already is must not create an undo step.
            Assert.False(new MoveTrackCommand(project, v1, 0).ChangesOrder, "no-op move");

            // Out-of-range drops clamp instead of throwing.
            var clamped = new MoveTrackCommand(project, v1, 99);
            clamped.Execute();
            Assert.Equal("V1", project.Tracks[^1].Name, "clamped to the last position");
            clamped.Undo();
            Assert.Equal("V1", project.Tracks[0].Name, "and back again");
        });

        TestRunner.Add("Tracks: lane order breaks layer ties, top lane at the bottom", () =>
        {
            var project = new Project();
            var upper = new Track { Name = "V1", Type = TrackType.Video };
            var lower = new Track { Name = "V2", Type = TrackType.Video };
            project.Tracks.Add(upper);
            project.Tracks.Add(lower);

            // Same layer on both clips: the lower lane wins (painted last).
            upper.Events.Add(new TimelineEvent { Name = "top-lane", Start = 0, Duration = 5 });
            lower.Events.Add(new TimelineEvent { Name = "bottom-lane", Start = 0, Duration = 5 });

            var order = FrameCompositor.EnumerateVisibleLayers(project, 1);
            Assert.Equal("top-lane", order[0].Event.Name, "the top lane sits underneath");
            Assert.Equal("bottom-lane", order[^1].Event.Name, "the lower lane covers it");

            // Reordering the lanes flips the stack.
            new MoveTrackCommand(project, lower, 0).Execute();
            order = FrameCompositor.EnumerateVisibleLayers(project, 1);
            Assert.Equal("top-lane", order[^1].Event.Name, "after the move the other lane is on top");
        });
    }
}
