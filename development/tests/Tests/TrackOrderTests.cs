using VideoEditor.Application.Commands;
using VideoEditor.Application.Editing;
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

        TestRunner.Add("Clips: move to another lane, and back on undo", () =>
        {
            var project = new Project();
            var t1 = new Track { Name = "T1", Type = TrackType.Overlay };
            var t2 = new Track { Name = "T2", Type = TrackType.Overlay };
            project.Tracks.Add(t1);
            project.Tracks.Add(t2);

            var title = new TimelineEvent
            {
                Name = "Title", Start = 2, Duration = 3, SourceOut = 3,
                Text = new TextStyle { Content = "Hello" }
            };
            t1.Events.Add(title);

            var move = new MoveEventCommand(title, t1, t2, newStart: 5);
            move.Execute();
            Assert.True(t1.Events.Count == 0 && t2.Events.Count == 1, "the clip changed lane");
            Assert.Close(5, t2.Events[0].Start, "and moved in time", 0.0001);

            move.Undo();
            Assert.True(t2.Events.Count == 0 && t1.Events.Count == 1, "undo puts it back");
            Assert.Close(2, t1.Events[0].Start, "with its original start", 0.0001);
        });

        TestRunner.Add("Clips: lanes only accept what they can render", () =>
        {
            var project = new Project();
            var video = new Track { Name = "V1", Type = TrackType.Video };
            var overlay = new Track { Name = "T1", Type = TrackType.Overlay };
            var audio = new Track { Name = "A1", Type = TrackType.Audio };
            project.Tracks.Add(video);
            project.Tracks.Add(overlay);
            project.Tracks.Add(audio);

            var song = new MediaItem { Name = "song.mp3", Type = MediaType.Audio };
            var clip = new MediaItem { Name = "clip.mp4", Type = MediaType.Video };
            project.Media.Items.Add(song);
            project.Media.Items.Add(clip);

            // A title may sit on any visual lane, never on an audio lane.
            var title = new TimelineEvent { Text = new TextStyle { Content = "Hi" }, Duration = 2 };
            Assert.True(TrackRouting.Accepts(project, video, title), "titles fit a video lane");
            Assert.True(TrackRouting.Accepts(project, overlay, title), "titles fit an overlay lane");
            Assert.False(TrackRouting.Accepts(project, audio, title), "titles do not fit an audio lane");

            var sound = new TimelineEvent { MediaId = song.Id, Duration = 2 };
            Assert.True(TrackRouting.Accepts(project, audio, sound), "sound on an audio lane");
            Assert.False(TrackRouting.Accepts(project, overlay, sound), "sound not on an overlay lane");

            var footage = new TimelineEvent { MediaId = clip.Id, Duration = 2 };
            Assert.True(TrackRouting.Accepts(project, video, footage), "video on a video lane");
            Assert.True(TrackRouting.Accepts(project, overlay, footage), "video on an overlay lane");
            Assert.False(TrackRouting.Accepts(project, audio, footage), "video not on an audio lane");

            // A clip whose media is gone belongs nowhere rather than everywhere.
            var orphan = new TimelineEvent { MediaId = Guid.NewGuid(), Duration = 2 };
            Assert.False(TrackRouting.Accepts(project, video, orphan), "missing media is not routable");

            Assert.Equal(TrackType.Audio, TrackRouting.LaneKindFor(MediaType.Audio), "new lane for sound");
            Assert.Equal(TrackType.Video, TrackRouting.LaneKindFor(MediaType.Image), "new lane for stills");
        });
    }
}
