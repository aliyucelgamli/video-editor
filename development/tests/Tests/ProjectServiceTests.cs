using VideoEditor.Application.Commands;
using VideoEditor.Application.Services;
using VideoEditor.Application.UndoRedo;
using VideoEditor.Domain;
using VideoEditor.ProjectIO;

namespace VideoEditor.Tests;

public static class ProjectServiceTests
{
    private static ProjectService CreateService(out UndoRedoService undoRedo)
    {
        undoRedo = new UndoRedoService();
        return new ProjectService(new JsonProjectSerializer(), undoRedo);
    }

    public static void Register()
    {
        TestRunner.Add("ProjectService: new project has default V1/A1/T1 tracks and is clean", () =>
        {
            var service = CreateService(out _);
            Assert.Equal(3, service.Current.Tracks.Count, "Default track count");
            Assert.Equal(TrackType.Video, service.Current.Tracks[0].Type, "First track type");
            Assert.Equal(TrackType.Audio, service.Current.Tracks[1].Type, "Second track type");
            Assert.Equal(TrackType.Overlay, service.Current.Tracks[2].Type, "Third track type");
            Assert.False(service.IsDirty, "New project must not be dirty");
        });

        TestRunner.Add("ProjectService: executing a command marks the project dirty", () =>
        {
            var service = CreateService(out var undoRedo);
            undoRedo.ExecuteCommand(new AddTrackCommand(service.Current, new Track { Name = "V2" }));
            Assert.True(service.IsDirty, "Project must be dirty after an edit");
        });

        TestRunner.Add("ProjectService: save clears dirty and remembers the path; open restores state", () =>
        {
            var service = CreateService(out var undoRedo);
            undoRedo.ExecuteCommand(new AddTrackCommand(service.Current, new Track { Name = "V2", Type = TrackType.Video }));

            var path = Path.Combine(Path.GetTempPath(), $"veproj-test-{Guid.NewGuid():N}.veproj");
            try
            {
                service.SaveAs(path);
                Assert.False(service.IsDirty, "Save must clear dirty");
                Assert.Equal(path, service.CurrentFilePath!, "CurrentFilePath");

                service.NewProject();
                Assert.Equal(3, service.Current.Tracks.Count, "Back to defaults");

                service.Open(path);
                Assert.Equal(4, service.Current.Tracks.Count, "Loaded track count");
                Assert.False(service.IsDirty, "Open must not be dirty");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        });

        TestRunner.Add("ProjectService: Save without a path throws (SaveAs required)", () =>
        {
            var service = CreateService(out _);
            Assert.Throws<InvalidOperationException>(() => service.Save());
        });
    }
}
