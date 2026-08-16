using VideoEditor.Application.Actions;
using VideoEditor.Application.Settings;

namespace VideoEditor.Tests;

/// <summary>Action registry, shortcut map and settings persistence.</summary>
public static class ShortcutTests
{
    public static void Register()
    {
        TestRunner.Add("Actions: registry ids are unique, categories keep order", () =>
        {
            var ids = EditorActions.All.Select(a => a.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count(), "unique ids");
            Assert.True(EditorActions.All.All(a => a.Name.Length > 0 && a.Category.Length > 0),
                "every action has a name and category");

            var groups = EditorActions.GroupedByCategory();
            Assert.Equal("File", groups[0].Category, "first category");
            Assert.Equal(
                EditorActions.All.Select(a => a.Category).Distinct().Count(),
                groups.Count, "each category appears once");
            Assert.Equal(EditorActions.All.Count, groups.Sum(g => g.Actions.Count),
                "grouping loses no actions");

            // No two actions may claim the same default gesture, or one of them
            // silently never fires once the bindings are built.
            var defaults = EditorActions.All
                .SelectMany(a => a.DefaultGestures.Select(g => (Action: a.Id, Gesture: g)))
                .ToList();
            var clashes = defaults
                .GroupBy(d => d.Gesture, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => $"{g.Key} → {string.Join(", ", g.Select(d => d.Action))}")
                .ToList();
            Assert.True(clashes.Count == 0, "no duplicate default gestures: " + string.Join(" | ", clashes));
        });

        TestRunner.Add("Shortcuts: defaults, override, conflict steal and reset", () =>
        {
            var undo = EditorActions.All.First(a => a.Id == "edit.undo");
            var split = EditorActions.All.First(a => a.Id == "edit.split");
            var map = new ShortcutMap();

            Assert.Equal(2, map.GesturesFor(undo).Count, "undo has two default gestures");
            Assert.True(map.FindConflict(EditorActions.All, "Ctrl+Z", "edit.split") == undo,
                "conflict finds the default holder");
            Assert.True(map.FindConflict(EditorActions.All, "ctrl+z", "edit.split") == undo,
                "conflict lookup ignores case");
            Assert.True(map.FindConflict(EditorActions.All, "Ctrl+Z", "edit.undo") is null,
                "the holder itself is not a conflict");

            // Assign Ctrl+Z to split: undo loses just that gesture, split replaces its defaults.
            map.RemoveGesture(undo, "Ctrl+Z");
            map.SetGesture(split.Id, "Ctrl+Z");
            Assert.Equal(1, map.GesturesFor(undo).Count, "undo keeps its other gesture");
            Assert.Equal("Z", map.GesturesFor(undo)[0], "the plain Z remains");
            Assert.Equal("Ctrl+Z", map.GesturesFor(split)[0], "split took the gesture");

            var overrides = map.ToOverrides();
            var reloaded = new ShortcutMap(overrides);
            Assert.Equal("Ctrl+Z", reloaded.GesturesFor(split)[0], "overrides roundtrip");

            map.ResetAll();
            Assert.Equal(2, map.GesturesFor(undo).Count, "reset restores defaults");
            Assert.Equal("S", map.GesturesFor(split)[0], "reset restores split default");
        });

        TestRunner.Add("Settings: save/load roundtrip, corrupt file falls back", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), $"ve_settings_{Guid.NewGuid():N}");
            try
            {
                var service = new SettingsService(directory);
                Assert.True(service.Load().UseHardwareEncoderByDefault, "defaults when missing");

                var settings = new AppSettings
                {
                    DefaultExportFolder = @"D:\Exports",
                    UseHardwareEncoderByDefault = false,
                    Shortcuts = { ["edit.split"] = new[] { "Ctrl+K" } }
                };
                service.Save(settings);

                var loaded = service.Load();
                Assert.Equal(@"D:\Exports", loaded.DefaultExportFolder!, "folder roundtrips");
                Assert.False(loaded.UseHardwareEncoderByDefault, "flag roundtrips");
                Assert.Equal("Ctrl+K", loaded.Shortcuts["edit.split"][0], "shortcuts roundtrip");

                File.WriteAllText(Path.Combine(directory, "settings.json"), "{ not json !!");
                Assert.True(service.Load().UseHardwareEncoderByDefault, "corrupt file loads defaults");
            }
            finally
            {
                try { Directory.Delete(directory, recursive: true); } catch { /* temp */ }
            }
        });
    }
}
