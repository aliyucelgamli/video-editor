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
