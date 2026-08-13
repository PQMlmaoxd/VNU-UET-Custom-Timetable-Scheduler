namespace Scheduler.Diagnostics;

internal static class ArgumentParser
{
    public static DiagnosticsOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        DiagnosticsCommand? command = null;
        string? workerPath = null;
        string? appPath = null;
        string? workbookPath = null;
        string? outputPath = null;
        var format = DiagnosticsOutputFormat.Text;
        var includePaths = false;
        var includeFileHashes = false;
        var verbosePrivate = false;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (string.IsNullOrWhiteSpace(argument))
            {
                throw new DiagnosticsUsageException();
            }

            if (argument is "-h" or "--help")
            {
                if (command is not null && command != DiagnosticsCommand.Help)
                {
                    throw new DiagnosticsUsageException();
                }

                command = DiagnosticsCommand.Help;
                continue;
            }

            if (argument == "--version")
            {
                if (command is not null && command != DiagnosticsCommand.Version)
                {
                    throw new DiagnosticsUsageException();
                }

                command = DiagnosticsCommand.Version;
                continue;
            }

            if (TryGetOption(argument, "--format", out var inlineFormat))
            {
                format = ParseFormat(inlineFormat ?? ReadValue(arguments, ref index));
                continue;
            }

            if (TryGetOption(argument, "--output", out var inlineOutput))
            {
                outputPath = SetOnce(outputPath, inlineOutput ?? ReadValue(arguments, ref index));
                continue;
            }

            if (TryGetOption(argument, "--worker", out var inlineWorker))
            {
                workerPath = SetOnce(workerPath, inlineWorker ?? ReadValue(arguments, ref index));
                continue;
            }

            if (TryGetOption(argument, "--app", out var inlineApp))
            {
                appPath = SetOnce(appPath, inlineApp ?? ReadValue(arguments, ref index));
                continue;
            }

            if (TryGetOption(argument, "--workbook", out var inlineWorkbook))
            {
                workbookPath = SetOnce(workbookPath, inlineWorkbook ?? ReadValue(arguments, ref index));
                continue;
            }

            switch (argument)
            {
                case "--include-paths":
                    includePaths = true;
                    continue;
                case "--include-file-hashes":
                    includeFileHashes = true;
                    continue;
                case "--verbose-private":
                    verbosePrivate = true;
                    continue;
            }

            if (argument.StartsWith('-'))
            {
                throw new DiagnosticsUsageException();
            }

            if (command is not null)
            {
                throw new DiagnosticsUsageException();
            }

            command = ParseCommand(argument);
        }

        var parsedCommand = command ?? DiagnosticsCommand.Help;
        ValidateCommandOptions(parsedCommand, workerPath, appPath, workbookPath);
        return new DiagnosticsOptions(
            parsedCommand,
            workerPath,
            appPath,
            workbookPath,
            format,
            outputPath,
            includePaths,
            includeFileHashes,
            verbosePrivate);
    }

    private static string ReadValue(IReadOnlyList<string> arguments, ref int index)
    {
        if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]) ||
            arguments[index].StartsWith('-'))
        {
            throw new DiagnosticsUsageException();
        }

        return arguments[index];
    }

    private static string SetOnce(string? existing, string value)
    {
        if (existing is not null || string.IsNullOrWhiteSpace(value))
        {
            throw new DiagnosticsUsageException();
        }

        return value;
    }

    private static bool TryGetOption(string argument, string option, out string? value)
    {
        if (argument == option)
        {
            value = null;
            return true;
        }

        var prefix = option + "=";
        if (argument.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = argument[prefix.Length..];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new DiagnosticsUsageException();
            }

            return true;
        }

        value = null;
        return false;
    }

    private static DiagnosticsOutputFormat ParseFormat(string value) => value switch
    {
        "text" => DiagnosticsOutputFormat.Text,
        "json" => DiagnosticsOutputFormat.Json,
        _ => throw new DiagnosticsUsageException(),
    };

    private static DiagnosticsCommand ParseCommand(string value) => value switch
    {
        "help" => DiagnosticsCommand.Help,
        "version" => DiagnosticsCommand.Version,
        "self-test" => DiagnosticsCommand.SelfTest,
        "worker" => DiagnosticsCommand.Worker,
        "app" => DiagnosticsCommand.App,
        "workbook" => DiagnosticsCommand.Workbook,
        "doctor" => DiagnosticsCommand.Doctor,
        _ => throw new DiagnosticsUsageException(),
    };

    private static void ValidateCommandOptions(
        DiagnosticsCommand command,
        string? workerPath,
        string? appPath,
        string? workbookPath)
    {
        switch (command)
        {
            case DiagnosticsCommand.Worker when workerPath is null || appPath is not null || workbookPath is not null:
            case DiagnosticsCommand.App when appPath is null || workerPath is not null || workbookPath is not null:
            case DiagnosticsCommand.Workbook when workbookPath is null || workerPath is not null || appPath is not null:
            case DiagnosticsCommand.Help or DiagnosticsCommand.Version or DiagnosticsCommand.SelfTest
                when workerPath is not null || appPath is not null || workbookPath is not null:
                throw new DiagnosticsUsageException();
            case DiagnosticsCommand.Doctor:
                return;
        }
    }
}
