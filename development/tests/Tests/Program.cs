namespace VideoEditor.Tests;

public static class Program
{
    public static int Main()
    {
        Console.WriteLine("VideoEditor test suite");
        Console.WriteLine("======================");

        UndoRedoTests.Register();
        SplitEventTests.Register();
        SerializationTests.Register();
        ProjectServiceTests.Register();
        EffectSystemTests.Register();
        TimelineModelTests.Register();
        TransformGizmoTests.Register();
        FadeTests.Register();
        TrimSlipTests.Register();
        ShortcutTests.Register();
        PreviewSelectionTests.Register();
        LayerTests.Register();
        TrackOrderTests.Register();
        ClipboardTests.Register();
        TimelineNavigationTests.Register();
        MediaEngineTests.Register();
        DiagnosticsTests.Register();
        FfmpegIntegrationTests.Register();

        return TestRunner.RunAll();
    }
}
