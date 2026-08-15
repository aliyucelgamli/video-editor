using VideoEditor.Domain;
using VideoEditor.ProjectIO;

namespace VideoEditor.Tests;

public static class SerializationTests
{
    public static void Register()
    {
        TestRunner.Add("ProjectIO: save/load round-trip preserves the model", () =>
        {
            var media = new MediaItem
            {
                Name = "video.mp4", FilePath = @"C:\Media\video.mp4",
                Type = MediaType.Video, DurationSeconds = 120.5, Tags = { "intro" }
            };

            var evt = new TimelineEvent
            {
                MediaId = media.Id, Name = "video.mp4",
                Start = 3.5, Duration = 10, SourceIn = 2, SourceOut = 12,
                PlaybackRate = 1.5, FadeInDuration = 0.5, FadeOutEasing = EasingType.OutBack,
                Opacity = 0.8,
                Text = new TextStyle { Content = "Hello", FontSize = 72, Bold = false },
                Transform = { PositionX = 100, ScaleX = 0.5, Rotation = 45 },
                Effects = { new EffectInstance { Type = "blur", Parameters = { ["radius"] = 4 } } },
                Keyframes =
                {
                    new KeyframeTrack
                    {
                        Property = "opacity",
                        Keyframes =
                        {
                            new Keyframe { Time = 0, Value = 0 },
                            new Keyframe { Time = 2, Value = 1, Interpolation = KeyframeInterpolation.EaseOut }
                        }
                    }
                }
            };

            var project = new Project
            {
                Settings = new ProjectSettings { Name = "Round Trip", Width = 1280, Height = 720, FrameRate = 60 },
                Media = { Items = { media } },
                Tracks =
                {
                    new Track { Name = "V1", Type = TrackType.Video, Events = { evt }, Volume = 0.9, Muted = true }
                },
                Markers = { new Marker { Name = "Intro", Time = 5, Comment = "start here" } }
            };

            var serializer = new JsonProjectSerializer();
            var path = Path.Combine(Path.GetTempPath(), $"veproj-test-{Guid.NewGuid():N}.veproj");
            try
            {
                serializer.Save(project, path);
                var loaded = serializer.Load(path);

                Assert.Equal("Round Trip", loaded.Settings.Name, "Settings.Name");
                Assert.Equal(1280, loaded.Settings.Width, "Settings.Width");
                Assert.Close(60, loaded.Settings.FrameRate, "Settings.FrameRate");

                Assert.Equal(1, loaded.Media.Items.Count, "Media count");
                Assert.Equal(media.Id, loaded.Media.Items[0].Id, "Media id");
                Assert.Equal(MediaType.Video, loaded.Media.Items[0].Type, "Media type");
                Assert.Close(120.5, loaded.Media.Items[0].DurationSeconds!.Value, "Media duration");

                Assert.Equal(1, loaded.Tracks.Count, "Track count");
                var track = loaded.Tracks[0];
                Assert.Equal(TrackType.Video, track.Type, "Track type");
                Assert.True(track.Muted, "Track muted");

                Assert.Equal(1, track.Events.Count, "Event count");
                var loadedEvt = track.Events[0];
                Assert.Close(3.5, loadedEvt.Start, "Event start");
                Assert.Close(1.5, loadedEvt.PlaybackRate, "Playback rate");
                Assert.Equal(EasingType.OutBack, loadedEvt.FadeOutEasing, "Fade easing");
                Assert.True(loadedEvt.Text is { Content: "Hello", FontSize: 72, Bold: false },
                    "Text style roundtrips");
                Assert.Close(100, loadedEvt.Transform.PositionX, "Transform.PositionX");
                Assert.Close(45, loadedEvt.Transform.Rotation, "Transform.Rotation");
                Assert.Equal("blur", loadedEvt.Effects[0].Type, "Effect type");
                Assert.Close(4, loadedEvt.Effects[0].Parameters["radius"], "Effect parameter");
                Assert.Equal(2, loadedEvt.Keyframes[0].Keyframes.Count, "Keyframe count");
                Assert.Equal(KeyframeInterpolation.EaseOut, loadedEvt.Keyframes[0].Keyframes[1].Interpolation, "Keyframe interpolation");

                Assert.Equal(1, loaded.Markers.Count, "Marker count");
                Assert.Equal("Intro", loaded.Markers[0].Name, "Marker name");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        });

        TestRunner.Add("ProjectIO: rejects a newer format version", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"veproj-test-{Guid.NewGuid():N}.veproj");
            try
            {
                File.WriteAllText(path, """{ "formatVersion": 999, "project": { } }""");
                Assert.Throws<ProjectFormatException>(() => new JsonProjectSerializer().Load(path));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        });

        TestRunner.Add("ProjectIO: rejects invalid JSON", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"veproj-test-{Guid.NewGuid():N}.veproj");
            try
            {
                File.WriteAllText(path, "this is not json {");
                Assert.Throws<ProjectFormatException>(() => new JsonProjectSerializer().Load(path));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        });
    }
}
