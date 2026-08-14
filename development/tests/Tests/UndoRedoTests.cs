using VideoEditor.Application.Commands;
using VideoEditor.Application.UndoRedo;
using VideoEditor.Domain;

namespace VideoEditor.Tests;

public static class UndoRedoTests
{
    public static void Register()
    {
        TestRunner.Add("UndoRedo: execute pushes undo and clears redo", () =>
        {
            var service = new UndoRedoService();
            var project = new Project();

            service.ExecuteCommand(new AddTrackCommand(project, new Track { Name = "V1" }));
            service.Undo();
            Assert.True(service.CanRedo, "Redo should be available after undo.");

            service.ExecuteCommand(new AddTrackCommand(project, new Track { Name = "V2" }));
            Assert.False(service.CanRedo, "Redo must be cleared by a new command.");
        });

        TestRunner.Add("UndoRedo: undo/redo restores track list", () =>
        {
            var service = new UndoRedoService();
            var project = new Project();
            var track = new Track { Name = "V1", Type = TrackType.Video };

            service.ExecuteCommand(new AddTrackCommand(project, track));
            Assert.Equal(1, project.Tracks.Count, "Track count after execute");

            service.Undo();
            Assert.Equal(0, project.Tracks.Count, "Track count after undo");

            service.Redo();
            Assert.Equal(1, project.Tracks.Count, "Track count after redo");
            Assert.Equal("V1", project.Tracks[0].Name, "Track name after redo");
        });

        TestRunner.Add("UndoRedo: composite command undoes in reverse order", () =>
        {
            var service = new UndoRedoService();
            var project = new Project();
            var track = new Track { Name = "V1" };
            var evt = new TimelineEvent { Name = "clip", Start = 0, Duration = 5 };

            service.ExecuteCommand(new CompositeCommand("Add track with event", new IEditorCommand[]
            {
                new AddTrackCommand(project, track),
                new AddEventCommand(track, evt)
            }));
            Assert.Equal(1, project.Tracks.Count, "Tracks after composite");
            Assert.Equal(1, track.Events.Count, "Events after composite");

            service.Undo();
            Assert.Equal(0, project.Tracks.Count, "Tracks after composite undo");
            Assert.Equal(0, track.Events.Count, "Events after composite undo");
        });

        TestRunner.Add("UndoRedo: move event between tracks round-trips", () =>
        {
            var service = new UndoRedoService();
            var trackA = new Track { Name = "V1" };
            var trackB = new Track { Name = "V2" };
            var evt = new TimelineEvent { Name = "clip", Start = 2, Duration = 5 };
            trackA.Events.Add(evt);

            service.ExecuteCommand(new MoveEventCommand(evt, trackA, trackB, 10));
            Assert.Equal(0, trackA.Events.Count, "Source track after move");
            Assert.Equal(1, trackB.Events.Count, "Target track after move");
            Assert.Close(10, evt.Start, "Start after move");

            service.Undo();
            Assert.Equal(1, trackA.Events.Count, "Source track after undo");
            Assert.Equal(0, trackB.Events.Count, "Target track after undo");
            Assert.Close(2, evt.Start, "Start after undo");
        });
    }
}
