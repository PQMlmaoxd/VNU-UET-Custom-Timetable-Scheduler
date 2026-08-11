using System.IO;

namespace Scheduler.Desktop;

public static class WebAssetLocator
{
    public static string Find(IReadOnlyList<string> arguments)
    {
        var candidates = new List<string?>
        {
            Path.Combine(AppContext.BaseDirectory, "web"),
        };

#if DEBUG
        candidates.InsertRange(0,
        [
            FindCommandLineWebRoot(arguments),
            Environment.GetEnvironmentVariable("SCHEDULER_WEB_ROOT"),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "frontend", "dist")),
        ]);
#endif

        foreach (var candidate in candidates.Where(static candidate => !string.IsNullOrWhiteSpace(candidate)))
        {
            var resolved = Path.GetFullPath(candidate!);
            if (File.Exists(Path.Combine(resolved, "index.html")))
            {
                return resolved;
            }
        }

        throw new DirectoryNotFoundException("Packaged React web assets were not found under web/.");
    }

    private static string? FindCommandLineWebRoot(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], "--web-root", StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }
}
