using VideoEditor.Application.Commands;
using VideoEditor.Application.UndoRedo;
using VideoEditor.Domain;

namespace VideoEditor.Tests;

public static class TimelineModelTests
{
    public static void Register()
    {
        TestRunner.Add("Volume: limits clamp to 0%..200%", () =>
        {
            Assert.Close(0, VolumeLimits.Clamp(-1), "below min");
            Assert.Close(2, VolumeLimits.Clamp(9), "above max");
            Assert.Close(1, VolumeLimits.Default, "default is 100%");
        });

        TestRunner.Add("Volume: SetValueCommand changes and restores event volume", () =>
        {
            var undo = new UndoRedoService();
            var evt = new TimelineEvent { Name = "clip" };
            Assert.Close(1.0, evt.Volume, "default");

            undo.ExecuteCommand(new SetValueCommand<double>(
                "Set volume", evt.Volume, VolumeLimits.Clamp(1.5), v => evt.Volume = v));
            Assert.Close(1.5, evt.Volume, "after set");

            undo.Undo();
            Assert.Close(1.0, evt.Volume, "after undo");
            undo.Redo();
            Assert.Close(1.5, evt.Volume, "after redo");
        });

        TestRunner.Add("ExportRange: normalized swaps and clamps", () =>
        {
            var range = new TimeRange { Start = 10, End = 4 }.Normalized();
            Assert.Close(4, range.Start);
            Assert.Close(10, range.End);
            Assert.Close(6, range.Duration);

            var negative = new TimeRange { Start = -5, End = 3 }.Normalized();
            Assert.Close(0, negative.Start);
        });

        TestRunner.Add("ExportRange: undoable set/clear on the project", () =>
        {
            var undo = new UndoRedoService();
            var project = new Project();

            undo.ExecuteCommand(new SetValueCommand<TimeRange?>(
                "Set export range", project.ExportRange,
                new TimeRange { Start = 1, End = 5 }, r => project.ExportRange = r));
            Assert.True(project.ExportRange is { } r1 && Math.Abs(r1.Start - 1) < 1e-9, "set");

            undo.ExecuteCommand(new SetValueCommand<TimeRange?>(
                "Clear export range", project.ExportRange, null, r => project.ExportRange = r));
            Assert.True(project.ExportRange is null, "cleared");

            undo.Undo();
            Assert.True(project.ExportRange is { } r2 && Math.Abs(r2.End - 5) < 1e-9, "undo clear");
            undo.Undo();
            Assert.True(project.ExportRange is null, "undo set");
        });

        TestRunner.Add("Stretch: shorter duration speeds playback up, undo restores", () =>
        {
            var undo = new UndoRedoService();
            // 10 s of source shown over 10 s of timeline at 1x.
            var evt = new TimelineEvent
            {
                Name = "clip", Start = 5, Duration = 10, SourceIn = 0, SourceOut = 10, PlaybackRate = 1.0
            };

            // Shift-drag the right edge in: 10 s of source in 5 s → 2x speed.
            undo.ExecuteCommand(new StretchEventCommand(evt, newStart: 5, newDuration: 5));
            Assert.Close(5, evt.Duration, "shortened duration");
            Assert.Close(2.0, evt.PlaybackRate, "doubled speed");
            Assert.Close(10, evt.SourceOut, "source untouched (non-destructive)");

            // Stretch out to 20 s → 0.5x slow motion.
            undo.ExecuteCommand(new StretchEventCommand(evt, newStart: 5, newDuration: 20));
            Assert.Close(0.5, evt.PlaybackRate, "half speed");

            undo.Undo();
            Assert.Close(2.0, evt.PlaybackRate, "undo second stretch");
            undo.Undo();
            Assert.Close(1.0, evt.PlaybackRate, "undo first stretch");
            Assert.Close(10, evt.Duration, "original duration restored");
        });

        TestRunner.Add("Stretch: duration clamps to the minimum", () =>
        {
            var evt = new TimelineEvent { Name = "clip", Duration = 4, SourceOut = 4 };
            new StretchEventCommand(evt, 0, 0.001).Execute();
            Assert.Close(StretchEventCommand.MinDuration, evt.Duration, "clamped duration");
        });

        TestRunner.Add("Linked events: composite move keeps A/V in sync", () =>
        {
            var undo = new UndoRedoService();
            var videoTrack = new Track { Name = "V1", Type = TrackType.Video };
            var audioTrack = new Track { Name = "A1", Type = TrackType.Audio };

            var video = new TimelineEvent { Name = "clip", Start = 2, Duration = 5 };
            var audio = new TimelineEvent { Name = "clip (audio)", Start = 2, Duration = 5 };
            video.LinkedEventId = audio.Id;
            audio.LinkedEventId = video.Id;
            videoTrack.Events.Add(video);
            audioTrack.Events.Add(audio);

            undo.ExecuteCommand(new CompositeCommand("Move linked clip", new IEditorCommand[]
            {
                new MoveEventCommand(video, videoTrack, videoTrack, 8),
                new MoveEventCommand(audio, audioTrack, audioTrack, 8)
            }));
            Assert.Close(8, video.Start, "video moved");
            Assert.Close(8, audio.Start, "audio moved");

            undo.Undo();
            Assert.Close(2, video.Start, "video restored");
            Assert.Close(2, audio.Start, "audio restored");
        });
    }
}
