using VideoEditor.Application.Editing;
using VideoEditor.Domain;
using VideoEditor.MediaEngine.Frames;

namespace VideoEditor.Tests;

/// <summary>Edge trim / slip math and the text raster cache.</summary>
public static class TrimSlipTests
{
    public static void Register()
    {
        TestRunner.Add("Trim: right edge moves duration and sourceOut, rate fixed", () =>
        {
            var evt = new TimelineEvent
            {
                Start = 2, Duration = 10, SourceIn = 5, SourceOut = 25, PlaybackRate = 2
            };

            var trim = EdgeTrim.BuildTrim(evt, mediaDuration: 30, fromLeftEdge: false,
                newStart: 2, newDuration: 6);
            trim.Execute();
            Assert.Close(2, evt.Start, "start untouched");
            Assert.Close(6, evt.Duration, "duration trimmed");
            Assert.Close(17, evt.SourceOut, "sourceOut follows at 2x rate");
            Assert.Close(2, evt.PlaybackRate, "rate fixed");

            trim.Undo();
            Assert.Close(10, evt.Duration, "undo restores duration");
            Assert.Close(25, evt.SourceOut, "undo restores sourceOut");

            // Extending beyond the media clamps to the available source.
            var clamped = EdgeTrim.BuildTrim(evt, mediaDuration: 30, fromLeftEdge: false,
                newStart: 2, newDuration: 60);
            clamped.Execute();
            Assert.Close(12.5, evt.Duration, "clamped to (30 - 5) / 2x");
            clamped.Undo();
        });

        TestRunner.Add("Trim: left edge moves start and sourceIn, clamped at source zero", () =>
        {
            var evt = new TimelineEvent
            {
                Start = 4, Duration = 10, SourceIn = 2, SourceOut = 12, PlaybackRate = 1
            };

            var trim = EdgeTrim.BuildTrim(evt, mediaDuration: 20, fromLeftEdge: true,
                newStart: 6, newDuration: 0);
            trim.Execute();
            Assert.Close(6, evt.Start, "start moved right");
            Assert.Close(8, evt.Duration, "duration shrank");
            Assert.Close(4, evt.SourceIn, "sourceIn follows");
            trim.Undo();

            // Dragging further left than the source has footage stops at sourceIn = 0.
            var limited = EdgeTrim.BuildTrim(evt, mediaDuration: 20, fromLeftEdge: true,
                newStart: 0, newDuration: 0);
            limited.Execute();
            Assert.Close(2, evt.Start, "left edge stops where sourceIn hits 0");
            Assert.Close(0, evt.SourceIn, "sourceIn exactly 0");
            limited.Undo();
        });

        TestRunner.Add("Slip: source slides inside media bounds, position fixed", () =>
        {
            var evt = new TimelineEvent
            {
                Start = 3, Duration = 5, SourceIn = 4, SourceOut = 9, PlaybackRate = 1
            };

            var slip = EdgeTrim.BuildSlip(evt, mediaDuration: 20, deltaSeconds: 2)!;
            slip.Execute();
            Assert.Close(3, evt.Start, "timeline position fixed");
            Assert.Close(2, evt.SourceIn, "drag right shows earlier footage");
            Assert.Close(7, evt.SourceOut, "span preserved");
            slip.Undo();
            Assert.Close(4, evt.SourceIn, "undo restores source");

            var clamped = EdgeTrim.BuildSlip(evt, mediaDuration: 20, deltaSeconds: 99)!;
            clamped.Execute();
            Assert.Close(0, evt.SourceIn, "clamped at the media start");
            clamped.Undo();

            Assert.True(EdgeTrim.BuildSlip(evt, mediaDuration: 20, deltaSeconds: 0) is null,
                "no-op slip produces no command");
        });

        TestRunner.Add("Text: raster cache keys by style and size", () =>
        {
            var style = new TextStyle { Content = "Hello", FontSize = 96 };
            var other = new TextStyle { Content = "World", FontSize = 96 };
            Assert.False(
                TextRasterCache.KeyFor(style, 640, 360) == TextRasterCache.KeyFor(other, 640, 360),
                "different content, different key");
            Assert.False(
                TextRasterCache.KeyFor(style, 640, 360) == TextRasterCache.KeyFor(style, 1920, 1080),
                "different size, different key");

            var cache = new TextRasterCache();
            Assert.True(cache.TryGetShared(style, 640, 360) is null, "miss before store");
            cache.Store(style, 640, 360, new RawFrame(new byte[640 * 360 * 4], 640, 360));
            Assert.True(cache.TryGetShared(style, 640, 360) is not null, "hit after store");
            Assert.True(cache.TryGetShared(style, 1920, 1080) is null, "other size still misses");
        });
    }
}
