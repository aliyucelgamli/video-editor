using VideoEditor.Domain;
using VideoEditor.MediaEngine.Frames;

namespace VideoEditor.Tests;

/// <summary>Compositing order: layers decide what covers what.</summary>
public static class LayerTests
{
    public static void Register()
    {
        TestRunner.Add("Layers: defaults stack video under images under text", () =>
        {
            Assert.Equal(0, Layers.DefaultFor(MediaType.Video), "video default");
            Assert.Equal(0, Layers.DefaultFor(MediaType.Audio), "audio default");
            Assert.Equal(1, Layers.DefaultFor(MediaType.Image), "image default");
            Assert.True(Layers.Text > Layers.Image && Layers.Image > Layers.Video,
                "text over images over video");
            Assert.Equal(Layers.Max, Layers.Clamp(9999), "clamped high");
            Assert.Equal(Layers.Min, Layers.Clamp(-9999), "clamped low");
        });

        TestRunner.Add("Layers: paint order is back to front, text on top of video", () =>
        {
            // The default project layout: V1 first, overlay last — the very
            // arrangement that used to hide titles behind the footage.
            var project = new Project();
            var video = new Track { Name = "V1", Type = TrackType.Video };
            var audio = new Track { Name = "A1", Type = TrackType.Audio };
            var overlay = new Track { Name = "T1", Type = TrackType.Overlay };
            project.Tracks.Add(video);
            project.Tracks.Add(audio);
            project.Tracks.Add(overlay);

            var clip = new TimelineEvent { Name = "clip", Start = 0, Duration = 10, Layer = Layers.Video };
            var title = new TimelineEvent
            {
                Name = "title", Start = 0, Duration = 10,
                Layer = Layers.Text, Text = new TextStyle { Content = "Hi" }
            };
            video.Events.Add(clip);
            overlay.Events.Add(title);

            var order = FrameCompositor.EnumerateVisibleLayers(project, 5);
            Assert.Equal(2, order.Count, "both clips are visible");
            Assert.Equal("clip", order[0].Event.Name, "video is painted first (bottom)");
            Assert.Equal("title", order[^1].Event.Name, "text is painted last (on top)");

            // Raising a photo above the title flips them.
            var photo = new TimelineEvent { Name = "photo", Start = 0, Duration = 10, Layer = 5 };
            video.Events.Add(photo);
            order = FrameCompositor.EnumerateVisibleLayers(project, 5);
            Assert.Equal("photo", order[^1].Event.Name, "higher layer wins regardless of track");

            // A track layer lifts everything on that lane.
            video.Layer = 10;
            order = FrameCompositor.EnumerateVisibleLayers(project, 5);
            Assert.Equal("photo", order[^1].Event.Name, "track layer adds to its clips");
            Assert.Equal("title", order[0].Event.Name, "the title is now underneath");

            video.Layer = 0;
            Assert.Equal(1, Layers.Effective(overlay, new TimelineEvent { Layer = 1 }), "effective = track + clip");

            // Muted tracks drop out, and clips outside the time do too.
            overlay.Muted = true;
            Assert.False(FrameCompositor.EnumerateVisibleLayers(project, 5).Any(e => e.Event.Name == "title"),
                "muted lanes are skipped");
            overlay.Muted = false;
            Assert.Equal(0, FrameCompositor.EnumerateVisibleLayers(project, 50).Count,
                "nothing visible past the clips");
        });
    }
}
