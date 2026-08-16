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
            overlay.Events.Add(title);
            Assert.True(TrackRouting.Accepts(project, video, title, overlay), "titles fit a video lane");
            Assert.True(TrackRouting.Accepts(project, overlay, title, overlay), "titles fit an overlay lane");
            Assert.False(TrackRouting.Accepts(project, audio, title, overlay), "titles do not fit an audio lane");

            var sound = new TimelineEvent { MediaId = song.Id, Duration = 2 };
            audio.Events.Add(sound);
            Assert.True(TrackRouting.Accepts(project, audio, sound, audio), "sound on an audio lane");
            Assert.False(TrackRouting.Accepts(project, overlay, sound, audio), "sound not on an overlay lane");

            var footage = new TimelineEvent { MediaId = clip.Id, Duration = 2 };
            video.Events.Add(footage);
            Assert.True(TrackRouting.Accepts(project, video, footage, video), "video on a video lane");
            Assert.True(TrackRouting.Accepts(project, overlay, footage, video), "video on an overlay lane");
            Assert.False(TrackRouting.Accepts(project, audio, footage, video), "video not on an audio lane");

            // A clip whose media is gone belongs nowhere rather than everywhere.
            var orphan = new TimelineEvent { MediaId = Guid.NewGuid(), Duration = 2 };
            Assert.False(TrackRouting.Accepts(project, video, orphan, video), "missing media is not routable");

            Assert.Equal(TrackType.Audio, TrackRouting.LaneKindFor(MediaType.Audio), "new lane for sound");
            Assert.Equal(TrackType.Video, TrackRouting.LaneKindFor(MediaType.Image), "new lane for stills");
        });

        TestRunner.Add("Paste: a copy lands back on the lane it came from", () =>
        {
            var project = new Project();
            var t1 = new Track { Name = "T1", Type = TrackType.Overlay };
            var t2 = new Track { Name = "T2", Type = TrackType.Overlay };
            var t3 = new Track { Name = "T3", Type = TrackType.Overlay };
            project.Tracks.Add(t1);
            project.Tracks.Add(t2);
            project.Tracks.Add(t3);

            var title = new TimelineEvent { Text = new TextStyle { Content = "Hi" }, Duration = 2 };
            t2.Events.Add(title);

            // The bug this covers: every lane here accepts the clip, so "first
            // that accepts" silently sent duplicates to the topmost lane.
            var kind = TrackRouting.ClipKind.Of(project, t2, title);
            Assert.Equal(t2, TrackRouting.PreferredLane(project, kind, t2.Id),
                "the source lane wins over the topmost one");
            Assert.Equal(t3, TrackRouting.PreferredLane(project, kind, t3.Id),
                "and over any other lane too");

            // Source lane deleted since the copy: fall back rather than fail.
            project.Tracks.Remove(t2);
            Assert.Equal(t1, TrackRouting.PreferredLane(project, kind, t2.Id),
                "a missing source lane falls back to the first that accepts");

            // Source lane exists but cannot hold this clip: fall back as well.
            var audio = new Track { Name = "A1", Type = TrackType.Audio };
            project.Tracks.Add(audio);
            Assert.Equal(t1, TrackRouting.PreferredLane(project, kind, audio.Id),
                "a source lane that no longer suits the clip is not used");

            // Nothing suitable anywhere -> the caller has to make a lane.
            var audioOnly = new Project();
            audioOnly.Tracks.Add(new Track { Name = "A1", Type = TrackType.Audio });
            Assert.True(TrackRouting.PreferredLane(audioOnly, kind, Guid.NewGuid()) is null,
                "no lane can hold a title here");
        });

        TestRunner.Add("Linked pair: the sound half stays sound, on its own lane", () =>
        {
            // Importing a video with sound creates TWO events sharing ONE media
            // item: the picture on V1 and its linked sound on A1. Reading the
            // kind off the media calls that sound "video" — which is exactly how
            // duplicating an audio clip used to land a copy on the video lane.
            var project = new Project();
            var v1 = new Track { Name = "V1", Type = TrackType.Video };
            var v2 = new Track { Name = "V2", Type = TrackType.Video };
            var a1 = new Track { Name = "A1", Type = TrackType.Audio };
            var a2 = new Track { Name = "A2", Type = TrackType.Audio };
            project.Tracks.Add(v1);
            project.Tracks.Add(v2);
            project.Tracks.Add(a1);
            project.Tracks.Add(a2);

            var movie = new MediaItem { Name = "movie.mp4", Type = MediaType.Video, HasAudio = true };
            project.Media.Items.Add(movie);

            var picture = new TimelineEvent { MediaId = movie.Id, Start = 0, Duration = 5 };
            var sound = new TimelineEvent { MediaId = movie.Id, Start = 0, Duration = 5 };
            picture.LinkedEventId = sound.Id;
            sound.LinkedEventId = picture.Id;
            v1.Events.Add(picture);
            a1.Events.Add(sound);

            var soundKind = TrackRouting.ClipKind.Of(project, a1, sound);
            Assert.Equal(MediaType.Audio, soundKind.Media, "the lane it sits on says it is sound");
            Assert.True(soundKind.FitsOn(TrackType.Audio), "sound fits an audio lane");
            Assert.False(soundKind.FitsOn(TrackType.Video), "sound does not fit a video lane");

            var pictureKind = TrackRouting.ClipKind.Of(project, v1, picture);
            Assert.Equal(MediaType.Video, pictureKind.Media, "the picture half is still video");
            Assert.False(pictureKind.FitsOn(TrackType.Audio), "picture does not fit an audio lane");

            // Duplicating the sound must come back on A1, not on the topmost lane.
            Assert.Equal(a1, TrackRouting.PreferredLane(project, soundKind, a1.Id),
                "a duplicated sound clip stays on its own audio lane");
            Assert.Equal(v1, TrackRouting.PreferredLane(project, pictureKind, v1.Id),
                "and the picture half stays on its video lane");

            // Dragging the sound between lanes must offer audio lanes only.
            Assert.True(TrackRouting.Accepts(project, a2, sound, a1), "sound may move to another audio lane");
            Assert.False(TrackRouting.Accepts(project, v2, sound, a1), "sound may not move to a video lane");
        });
    }
}
