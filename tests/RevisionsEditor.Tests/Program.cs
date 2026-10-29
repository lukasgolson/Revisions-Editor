using System.Reflection;
using Xunit;

namespace RevisionsEditor.Tests;

/// <summary>
/// Console runner so the suite can run on the STA thread WPF needs: `dotnet run --project tests/RevisionsEditor.Tests`.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] filters)
    {
        Console.WriteLine("=== RevisionsEditor Automated Verification Suite ===");
        var suite = new RevisionsEditorTests();
        // Optional arguments: run only the tests whose names contain one of them.
        var methods = typeof(RevisionsEditorTests)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<FactAttribute>() != null)
            .Where(m => filters.Length == 0 || filters.Any(f => m.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        int passed = 0;
        int failed = 0;

        foreach (var method in methods)
        {
            // [Theory] derives from [Fact]; run it once per [InlineData] row.
            var rows = method.GetCustomAttributes<InlineDataAttribute>()
                .Select(d => d.GetData(method).Single())
                .DefaultIfEmpty(null)
                .ToList();

            foreach (object?[]? args in rows)
            {
                string name = args == null ? method.Name : $"{method.Name}({string.Join(", ", args.Select(a => $"\"{a}\""))})";
                try
                {
                    // WPF tests leave a dispatcher context on this thread; an async test awaiting on it while
                    // this thread blocks below would deadlock.
                    SynchronizationContext.SetSynchronizationContext(null);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    if (method.Invoke(suite, args) is Task task)
                    {
                        task.GetAwaiter().GetResult();
                    }
                    sw.Stop();
                    passed++;
                    Console.WriteLine($"  [PASS] {name} ({sw.ElapsedMilliseconds} ms)");
                }
                catch (Exception ex)
                {
                    failed++;
                    var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
                    Console.WriteLine($"  [FAIL] {name}: {inner.GetType().Name} - {inner.Message}");
                    Console.WriteLine(inner.StackTrace);
                }
            }
        }

        Console.WriteLine($"=== Summary: {passed} Passed, {failed} Failed (Total: {passed + failed}) ===");
        return failed == 0 ? 0 : 1;
    }
}
