using System.Diagnostics;
using System.Reflection;

namespace RhinoTrunk.Tests;

// A deliberately tiny, dependency-free test runner (no NuGet needed). Run with:
//   dotnet run --project tests/RhinoTrunk.Tests            (all tests)
//   dotnet run --project tests/RhinoTrunk.Tests -- Merge   (tests whose name contains "Merge")
// The API mirrors xUnit closely enough that moving to xUnit later is mechanical (see docs/OPEN_QUESTIONS.md).

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute;

public sealed class AssertionException(string message) : Exception(message);

public static class Assert
{
    public static void Equal<T>(T expected, T actual, string? because = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"Expected <{expected}> but got <{actual}>{Because(because)}");
    }

    public static void NotEqual<T>(T notExpected, T actual, string? because = null)
    {
        if (EqualityComparer<T>.Default.Equals(notExpected, actual))
            throw new AssertionException($"Did not expect <{actual}>{Because(because)}");
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? because = null)
    {
        var e = expected.ToList();
        var a = actual.ToList();
        if (!e.SequenceEqual(a))
            throw new AssertionException($"Expected [{string.Join(", ", e)}] but got [{string.Join(", ", a)}]{Because(because)}");
    }

    public static void True(bool condition, string? because = null)
    {
        if (!condition) throw new AssertionException("Expected true" + Because(because));
    }

    public static void False(bool condition, string? because = null)
    {
        if (condition) throw new AssertionException("Expected false" + Because(because));
    }

    public static T NotNull<T>(T? value, string? because = null) where T : class =>
        value ?? throw new AssertionException("Expected a value but got null" + Because(because));

    public static void Null(object? value, string? because = null)
    {
        if (value is not null) throw new AssertionException($"Expected null but got <{value}>{Because(because)}");
    }

    public static T Single<T>(IEnumerable<T> items, Func<T, bool>? predicate = null, string? because = null)
    {
        var matches = (predicate is null ? items : items.Where(predicate)).ToList();
        if (matches.Count != 1) throw new AssertionException($"Expected exactly one match but found {matches.Count}{Because(because)}");
        return matches[0];
    }

    public static void Contains<T>(IEnumerable<T> items, Func<T, bool> predicate, string? because = null)
    {
        if (!items.Any(predicate)) throw new AssertionException("No item matched" + Because(because));
    }

    public static void DoesNotContain<T>(IEnumerable<T> items, Func<T, bool> predicate, string? because = null)
    {
        if (items.Any(predicate)) throw new AssertionException("An item matched unexpectedly" + Because(because));
    }

    public static async Task<TException> ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException e)
        {
            return e;
        }
        catch (Exception e)
        {
            throw new AssertionException($"Expected {typeof(TException).Name} but got {e.GetType().Name}: {e.Message}");
        }
        throw new AssertionException($"Expected {typeof(TException).Name} but nothing was thrown");
    }

    private static string Because(string? because) => because is null ? "" : " (" + because + ")";
}

public static class TestRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var filter = args.FirstOrDefault();
        var tests = typeof(TestRunner).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.GetCustomAttribute<TestAttribute>() is not null).Select(m => (Type: t, Method: m)))
            .Where(x => filter is null || $"{x.Type.Name}.{x.Method.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Type.Name, StringComparer.Ordinal).ThenBy(x => x.Method.MetadataToken)
            .ToList();

        var failed = new List<string>();
        var total = Stopwatch.StartNew();
        foreach (var (type, method) in tests)
        {
            var name = $"{type.Name}.{method.Name}";
            var watch = Stopwatch.StartNew();
            object? instance = null;
            try
            {
                instance = Activator.CreateInstance(type);
                if (method.Invoke(instance, null) is Task task) await task;
                Console.WriteLine($"  PASS  {name} ({watch.ElapsedMilliseconds} ms)");
            }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException { InnerException: { } ie } ? ie : e;
                failed.Add(name);
                Console.WriteLine($"  FAIL  {name}: {inner.GetType().Name}: {inner.Message}");
                if (inner is not AssertionException) Console.WriteLine(inner.StackTrace);
            }
            finally
            {
                if (instance is IAsyncDisposable ad) await ad.DisposeAsync();
                else if (instance is IDisposable d) d.Dispose();
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{tests.Count - failed.Count} passed, {failed.Count} failed, {tests.Count} total in {total.Elapsed.TotalSeconds:F1} s");
        foreach (var f in failed) Console.WriteLine("  failed: " + f);
        return failed.Count == 0 && tests.Count > 0 ? 0 : 1;
    }
}
