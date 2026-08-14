using VideoEditor.Application.Commands;
using VideoEditor.Domain;

namespace VideoEditor.Tests;

public static class SplitEventTests
{
    public static void Register()
    {
        TestRunner.Add("Split: creates two adjacent events with continuous source range", () =>
        {
            var track = new Track { Name = "V1", Type = TrackType.Video };
            var evt = new TimelineEvent
            {
                Name = "clip", Start = 10, Duration = 20,
                SourceIn = 5, SourceOut = 25, FadeInDuration = 1, FadeOutDuration = 2
            };
            track.Events.Add(evt);

            var command = new SplitEventCommand(track, evt, splitTime: 18);
            command.Execute();
            var second = command.SecondEvent!;

            Assert.Equal(2, track.Events.Count, "Event count");
            Assert.Close(10, evt.Start, "First start");
            Assert.Close(8, evt.Duration, "First duration");
            Assert.Close(5, evt.SourceIn, "First sourceIn");
            Assert.Close(13, evt.SourceOut, "First sourceOut");
            Assert.Close(0, evt.FadeOutDuration, "First fadeOut moved to second");
            Assert.Close(1, evt.FadeInDuration, "First fadeIn kept");

            Assert.Close(18, second.Start, "Second start");
            Assert.Close(12, second.Duration, "Second duration");
            Assert.Close(13, second.SourceIn, "Second sourceIn");
            Assert.Close(25, second.SourceOut, "Second sourceOut");
            Assert.Close(2, second.FadeOutDuration, "Second fadeOut");
            Assert.Close(evt.End, second.Start, "Adjacency");
        });

        TestRunner.Add("Split: respects playback rate when mapping source position", () =>
        {
            var track = new Track();
            var evt = new TimelineEvent
            {
                Name = "fast", Start = 0, Duration = 10,
                SourceIn = 0, SourceOut = 20, PlaybackRate = 2.0
            };
            track.Events.Add(evt);

            var command = new SplitEventCommand(track, evt, splitTime: 4);
            command.Execute();

            Assert.Close(8, evt.SourceOut, "First sourceOut (4s * 2.0 rate)");
            Assert.Close(8, command.SecondEvent!.SourceIn, "Second sourceIn");
            Assert.Close(20, command.SecondEvent!.SourceOut, "Second sourceOut");
        });

        TestRunner.Add("Split: undo restores the original event exactly", () =>
        {
            var track = new Track();
            var evt = new TimelineEvent
            {
                Name = "clip", Start = 10, Duration = 20,
                SourceIn = 5, SourceOut = 25, FadeOutDuration = 2, FadeOutCurve = FadeCurve.Smooth
            };
            track.Events.Add(evt);

            var command = new SplitEventCommand(track, evt, 18);
            command.Execute();
            command.Undo();

            Assert.Equal(1, track.Events.Count, "Event count after undo");
            Assert.Close(20, evt.Duration, "Duration restored");
            Assert.Close(25, evt.SourceOut, "SourceOut restored");
            Assert.Close(2, evt.FadeOutDuration, "FadeOut restored");
            Assert.Equal(FadeCurve.Smooth, evt.FadeOutCurve, "FadeOutCurve restored");

            // Redo must produce a consistent split again.
            command.Execute();
            Assert.Equal(2, track.Events.Count, "Event count after redo");
        });

        TestRunner.Add("Split: rejects a time outside the event", () =>
        {
            var track = new Track();
            var evt = new TimelineEvent { Start = 10, Duration = 20 };
            track.Events.Add(evt);

            Assert.Throws<ArgumentOutOfRangeException>(() => new SplitEventCommand(track, evt, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SplitEventCommand(track, evt, 30));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SplitEventCommand(track, evt, 5));
        });
    }
}
