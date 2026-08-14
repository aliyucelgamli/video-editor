using VideoEditor.Application.Commands;
using VideoEditor.Application.Effects;
using VideoEditor.Application.UndoRedo;
using VideoEditor.Domain;
using VideoEditor.Domain.Effects;
using VideoEditor.ProjectIO;

namespace VideoEditor.Tests;

public static class EffectSystemTests
{
    public static void Register()
    {
        TestRunner.Add("Effects: catalog contains built-ins with valid targets", () =>
        {
            var catalog = new EffectCatalog();
            Assert.True(catalog.All.Count >= 10, "Expected at least 10 built-in effects.");
            Assert.True(catalog.Find("grayscale") is { Targets: EffectTarget.Visual });
            Assert.True(catalog.Find("helium") is { Targets: EffectTarget.Audio });
            Assert.True(catalog.Find("HELIUM") != null, "Lookup should be case-insensitive.");
            Assert.True(catalog.Find("nope") is null);
        });

        TestRunner.Add("Effects: definition creates instance with defaults and resolves steps", () =>
        {
            var catalog = new EffectCatalog();
            var blur = catalog.Find("blur")!;
            var instance = blur.CreateInstance();
            Assert.Equal("blur", instance.Type);
            Assert.Close(4, instance.Parameters["radius"], "default radius");

            instance.Parameters["radius"] = 12;
            var steps = blur.ResolveSteps(instance.Parameters);
            Assert.Equal(1, steps.Count, "step count");
            Assert.Equal("blur", steps[0].Kernel);
            Assert.Close(12, steps[0].Args["radius"], "resolved radius");
        });

        TestRunner.Add("Effects: parameter values are clamped to their range", () =>
        {
            var catalog = new EffectCatalog();
            var grayscale = catalog.Find("grayscale")!;
            var steps = grayscale.ResolveSteps(new Dictionary<string, double> { ["amount"] = 99 });
            Assert.Close(1, steps[0].Args["amount"], "clamped amount");
        });

        TestRunner.Add("Effects: CanApplyTo respects targets", () =>
        {
            var catalog = new EffectCatalog();
            Assert.True(catalog.Find("blur")!.CanApplyTo(MediaType.Video));
            Assert.True(catalog.Find("blur")!.CanApplyTo(MediaType.Image));
            Assert.False(catalog.Find("blur")!.CanApplyTo(MediaType.Audio));
            Assert.True(catalog.Find("helium")!.CanApplyTo(MediaType.Audio));
            Assert.False(catalog.Find("helium")!.CanApplyTo(MediaType.Video));
        });

        TestRunner.Add("Effects: add/remove/param commands are undoable", () =>
        {
            var undo = new UndoRedoService();
            var evt = new TimelineEvent { Name = "clip" };
            var catalog = new EffectCatalog();
            var instance = catalog.Find("blur")!.CreateInstance();

            undo.ExecuteCommand(new AddEffectCommand(evt.Effects, instance, "Blur", evt.Name));
            Assert.Equal(1, evt.Effects.Count, "after add");

            undo.ExecuteCommand(new SetEffectParameterCommand(instance, "radius", 9));
            Assert.Close(9, instance.Parameters["radius"], "after set");

            undo.ExecuteCommand(new SetEffectEnabledCommand(instance, false));
            Assert.False(instance.Enabled);

            undo.ExecuteCommand(new RemoveEffectCommand(evt.Effects, instance, "Blur"));
            Assert.Equal(0, evt.Effects.Count, "after remove");

            undo.Undo(); // remove
            Assert.Equal(1, evt.Effects.Count, "undo remove");
            undo.Undo(); // enabled
            Assert.True(instance.Enabled, "undo disable");
            undo.Undo(); // param
            Assert.Close(4, instance.Parameters["radius"], "undo param");
            undo.Undo(); // add
            Assert.Equal(0, evt.Effects.Count, "undo add");
        });

        TestRunner.Add("Effects: reorder command moves and restores", () =>
        {
            var undo = new UndoRedoService();
            var chain = new List<EffectInstance>
            {
                new() { Type = "a" }, new() { Type = "b" }, new() { Type = "c" }
            };
            undo.ExecuteCommand(new ReorderEffectCommand(chain, 0, 2));
            Assert.Equal("b,c,a", string.Join(",", chain.Select(e => e.Type)));
            undo.Undo();
            Assert.Equal("a,b,c", string.Join(",", chain.Select(e => e.Type)));
        });

        TestRunner.Add("Vefx: round-trips a custom effect definition", () =>
        {
            var effect = new EffectDefinition
            {
                Id = "dream",
                Name = "Dream Look",
                Category = "Custom",
                Targets = EffectTarget.Visual,
                Parameters =
                {
                    new EffectParameterDefinition { Key = "strength", Label = "Strength", Min = 0, Max = 1, Default = 0.6 }
                },
                Steps =
                {
                    new EffectStep { Kernel = "blur", Args = { ["radius"] = "3" } },
                    new EffectStep { Kernel = "temperature", Args = { ["amount"] = "$strength" } }
                }
            };

            var path = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.vefx");
            try
            {
                var serializer = new VefxSerializer();
                serializer.Save(effect, path);
                var loaded = serializer.Load(path);

                Assert.Equal("dream", loaded.Id);
                Assert.Equal(EffectTarget.Visual, loaded.Targets);
                Assert.Equal(2, loaded.Steps.Count, "steps");

                var steps = loaded.ResolveSteps(new Dictionary<string, double>());
                Assert.Close(3, steps[0].Args["radius"], "literal arg");
                Assert.Close(0.6, steps[1].Args["amount"], "default-bound arg");
            }
            finally { File.Delete(path); }
        });

        TestRunner.Add("Vefx: rejects invalid definitions", () =>
        {
            Assert.Throws<ProjectFormatException>(() => VefxSerializer.Parse("not json"));
            Assert.Throws<ProjectFormatException>(() => VefxSerializer.Parse("{}"));
            Assert.Throws<ProjectFormatException>(() =>
                VefxSerializer.Parse("""{"formatVersion":1,"effect":{"id":"x","name":"X","targets":"video","steps":[]}}"""));
        });

        TestRunner.Add("Vefx: user effect library imports into the catalog", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), $"vefx_{Guid.NewGuid():N}");
            var source = Path.Combine(Path.GetTempPath(), $"import_{Guid.NewGuid():N}.vefx");
            try
            {
                var serializer = new VefxSerializer();
                serializer.Save(new EffectDefinition
                {
                    Id = "robot-voice",
                    Name = "Robot Voice",
                    Targets = EffectTarget.Audio,
                    Steps = { new EffectStep { Kernel = "pitch", Args = { ["pitch"] = "0.8" } } }
                }, source);

                var catalog = new EffectCatalog();
                var library = new UserEffectLibrary(catalog, serializer, folder);
                var imported = library.Import(source);

                Assert.Equal("robot-voice", imported.Id);
                Assert.True(catalog.Find("robot-voice") != null, "registered in catalog");
                Assert.True(File.Exists(Path.Combine(folder, Path.GetFileName(source))), "copied to user folder");

                // A fresh catalog + LoadAll picks it up from disk.
                var catalog2 = new EffectCatalog();
                var library2 = new UserEffectLibrary(catalog2, serializer, folder);
                var (loaded, failed) = library2.LoadAll();
                Assert.Equal(1, loaded, "loaded count");
                Assert.Equal(0, failed.Count, "failed count");
            }
            finally
            {
                File.Delete(source);
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
        });
    }
}
