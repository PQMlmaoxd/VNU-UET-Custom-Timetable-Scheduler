using System.IO;
using System.Reflection;

namespace Scheduler.Desktop;

/// <summary>
/// Stable release identifiers shown to users and written to sanitized diagnostics.
/// Keep native solver provenance here rather than duplicating version strings across UI code.
/// </summary>
public static class DesktopReleaseMetadata
{
    public const string CadicalVersion = "3.0.1";
    public const string CadicalCommit = "c60730422e758ef1cebe7aeddf2dda31c996bf04";

    public static string SolverVersion => $"cadical-{CadicalVersion}";

    public static string ApplicationVersion
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(DesktopReleaseMetadata).Assembly;
            return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "unknown";
        }
    }

    public static string DefaultLogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SchedulerDesktop",
        "logs");
}
