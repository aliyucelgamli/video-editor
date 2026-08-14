namespace VideoEditor.Tests;

/// <summary>
/// Minimal zero-dependency test harness (NuGet-free so it runs anywhere).
/// Can be replaced by xUnit later without touching production code.
/// </summary>
public static class TestRunner
{
    private static readonly List<(string Name, Action Test)> Tests = new();
    private static readonly List<string> Failures = new();

    public static void Add(string name, Action test) => Tests.Add((name, test));

    public static int RunAll()
    {
        foreach (var (name, test) in Tests)
        {
            try
            {
                test();
                Console.WriteLine($"  PASS  {name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  FAIL  {name}");
                Console.WriteLine($"        {ex.Message}");
                Failures.Add(name);
            }
        }

        Console.WriteLine();
        Console.WriteLine(Failures.Count == 0
            ? $"All {Tests.Count} tests passed."
            : $"{Failures.Count}/{Tests.Count} tests FAILED.");
        return Failures.Count == 0 ? 0 : 1;
    }
}

public static class Assert
{
    public static void True(bool condition, string message = "Expected condition to be true.")
    {
        if (!condition) throw new Exception(message);
    }

    public static void False(bool condition, string message = "Expected condition to be false.")
    {
        if (condition) throw new Exception(message);
    }

    public static void Equal<T>(T expected, T actual, string? label = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{label ?? "Value"}: expected <{expected}>, got <{actual}>.");
    }

    public static void Close(double expected, double actual, string? label = null, double tolerance = 1e-9)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new Exception($"{label ?? "Value"}: expected <{expected}>, got <{actual}>.");
    }

    public static void Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        catch (Exception ex) { throw new Exception($"Expected {typeof(TException).Name}, got {ex.GetType().Name}."); }
        throw new Exception($"Expected {typeof(TException).Name}, but nothing was thrown.");
    }
}
