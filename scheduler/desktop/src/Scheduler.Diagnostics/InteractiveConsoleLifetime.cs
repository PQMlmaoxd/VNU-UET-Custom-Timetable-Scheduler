using System.Runtime.InteropServices;

namespace Scheduler.Diagnostics;

internal static class InteractiveConsoleLifetime
{
    public static bool ShouldPause(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 0 || Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            return false;
        }

        try
        {
            var processIds = new uint[2];
            return ShouldPause(arguments.Count, Console.IsInputRedirected, Console.IsOutputRedirected,
                GetConsoleProcessList(processIds, (uint)processIds.Length));
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    internal static bool ShouldPause(
        int argumentCount,
        bool inputRedirected,
        bool outputRedirected,
        uint attachedConsoleProcessCount) =>
        argumentCount == 0 && !inputRedirected && !outputRedirected && attachedConsoleProcessCount == 1;

    public static void Pause()
    {
        try
        {
            Console.WriteLine();
            Console.Write("Press any key to close...");
            Console.ReadKey(intercept: true);
            Console.WriteLine();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            // Losing the console must not change the completed diagnostic result.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);
}
