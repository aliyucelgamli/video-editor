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
        MediaEngineTests.Register();
        FfmpegIntegrationTests.Register();

        return TestRunner.RunAll();
    }
}
