namespace Scheduler.Diagnostics;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var shouldPause = InteractiveConsoleLifetime.ShouldPause(args);
        try
        {
            return await DiagnosticsApplication.RunAsync(args, Console.Out, Console.Error);
        }
        finally
        {
            if (shouldPause)
            {
                InteractiveConsoleLifetime.Pause();
            }
        }
    }
}
