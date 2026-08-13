namespace Scheduler.Diagnostics;

internal static class Program
{
    public static Task<int> Main(string[] args) =>
        DiagnosticsApplication.RunAsync(args, Console.Out, Console.Error);
}
