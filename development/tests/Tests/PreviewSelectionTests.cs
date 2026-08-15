using VideoEditor.Application.Effects;
using VideoEditor.Domain;
using VideoEditor.MediaEngine.Effects;
using VideoEditor.MediaEngine.Frames;

namespace VideoEditor.Tests;

/// <summary>Effect preview rendering (browsing the catalog never edits the project).</summary>
public static class PreviewSelectionTests
{
    public static void Register()
    {
        TestRunner.Add("Effect preview: renders like an attached effect, model untouched", () =>
        {
            var catalog = new EffectCatalog();
            var pipeline = new VideoEffectPipeline(catalog);
            var compositor = new FrameCompositor(new FrameExtractor(new MediaEngine.Ffmpeg.FFmpegLocator(".")), pipeline);

            var project = new Project();
            var track = new Track { Name = "V1", Type = TrackType.Video };
            project.Tracks.Add(track);
            var evt = new TimelineEvent { Start = 0, Duration = 5, SourceOut = 5 };
            track.Events.Add(evt);

            const int width = 4, height = 2;
            byte[] Layer()
            {
                var pixels = new byte[width * height * 4];
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    pixels[i] = 40;       // B
                    pixels[i + 1] = 120;  // G
                    pixels[i + 2] = 200;  // R
                    pixels[i + 3] = 255;
                }
                return pixels;
            }

            var plain = new byte[width * height * 4];
            FrameCompositor.FillBlack(plain);
            compositor.BlendLayerOnto(plain, Layer(), width, height, track, evt, 1, project);

            var grayscale = catalog.Find("grayscale")!;
            var previewed = new byte[width * height * 4];
            FrameCompositor.FillBlack(previewed);
            compositor.BlendLayerOnto(previewed, Layer(), width, height, track, evt, 1, project,
                new EffectPreview(evt.Id, grayscale.CreateInstance()));

            Assert.False(plain.SequenceEqual(previewed), "the preview changes the pixels");
            Assert.True(Math.Abs(previewed[0] - previewed[2]) <= 2, "previewed layer is grayscale");
            Assert.Equal(0, evt.Effects.Count, "the project model gained no effect");

            // A preview bound to another event must not touch this one.
            var otherEvent = new byte[width * height * 4];
            FrameCompositor.FillBlack(otherEvent);
            compositor.BlendLayerOnto(otherEvent, Layer(), width, height, track, evt, 1, project,
                new EffectPreview(Guid.NewGuid(), grayscale.CreateInstance()));
            Assert.True(plain.SequenceEqual(otherEvent), "preview only applies to its own event");
        });

        TestRunner.Add("Range: normalization keeps start before end for selections", () =>
        {
            // Dragging right-to-left must produce the same range as left-to-right.
            var backwards = new TimeRange { Start = 8, End = 3 }.Normalized();
            Assert.Close(3, backwards.Start, "start is the smaller edge");
            Assert.Close(8, backwards.End, "end is the larger edge");
            Assert.Close(5, backwards.Duration, "duration is positive");
        });
    }
}
