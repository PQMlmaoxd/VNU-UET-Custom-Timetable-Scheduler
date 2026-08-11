using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Scheduler.Desktop;

public enum DesktopThemePreference
{
    System,
    Light,
    Dark,
}

public interface IDesktopShellController
{
    void ApplyTheme(DesktopThemePreference preference);

    void MarkFrontendReady();
}

public sealed record DesktopThemePalette(
    Color Canvas,
    Color Surface,
    Color Text,
    Color Border,
    Color Brand);

public static class DesktopTheme
{
    private const string PreferenceFileName = "preferences.json";
    private const string ThemePreferenceKey = "scheduler.theme";
    private const string WindowsThemeRegistryPath =
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static DesktopThemePreference LoadPreference()
    {
        try
        {
            var path = PreferencePath();
            if (!File.Exists(path))
            {
                return DesktopThemePreference.System;
            }

            var stored = JsonSerializer.Deserialize<StoredThemePreference>(File.ReadAllText(path));
            return ParsePreference(stored?.Preference);
        }
        catch (IOException)
        {
            return DesktopThemePreference.System;
        }
        catch (JsonException)
        {
            return DesktopThemePreference.System;
        }
        catch (UnauthorizedAccessException)
        {
            return DesktopThemePreference.System;
        }
    }

    public static void SavePreference(DesktopThemePreference preference)
    {
        try
        {
            var directory = Path.GetDirectoryName(PreferencePath())
                ?? throw new InvalidOperationException("Desktop preference directory is unavailable.");
            Directory.CreateDirectory(directory);

            var temporaryPath = Path.Combine(directory, $"{PreferenceFileName}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new StoredThemePreference(ToWireValue(preference))));
            File.Move(temporaryPath, PreferencePath(), overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A theme choice must not stop the user from working if local storage is unavailable.
        }
    }

    public static DesktopThemePreference ParsePreference(string? value) => value switch
    {
        "system" => DesktopThemePreference.System,
        "light" => DesktopThemePreference.Light,
        "dark" => DesktopThemePreference.Dark,
        _ => DesktopThemePreference.System,
    };

    public static string ToWireValue(DesktopThemePreference preference) => preference switch
    {
        DesktopThemePreference.System => "system",
        DesktopThemePreference.Light => "light",
        DesktopThemePreference.Dark => "dark",
        _ => throw new ArgumentOutOfRangeException(nameof(preference), preference, "Unsupported desktop theme preference."),
    };

    public static DesktopThemePreference Resolve(DesktopThemePreference preference)
    {
        if (preference != DesktopThemePreference.System)
        {
            return preference;
        }

        var appsUseLightTheme = Registry.GetValue(WindowsThemeRegistryPath, "AppsUseLightTheme", 1);
        return appsUseLightTheme is int value && value == 0
            ? DesktopThemePreference.Dark
            : DesktopThemePreference.Light;
    }

    public static DesktopThemePalette PaletteFor(DesktopThemePreference preference) => Resolve(preference) switch
    {
        DesktopThemePreference.Dark => new DesktopThemePalette(
            Color.FromRgb(0x0B, 0x12, 0x20),
            Color.FromRgb(0x11, 0x1C, 0x2D),
            Color.FromRgb(0xF2, 0xF4, 0xF7),
            Color.FromRgb(0x29, 0x38, 0x4D),
            Color.FromRgb(0x7A, 0xA9, 0xD9)),
        _ => new DesktopThemePalette(
            Color.FromRgb(0xF2, 0xF4, 0xF7),
            Color.FromRgb(0xFF, 0xFF, 0xFF),
            Color.FromRgb(0x10, 0x18, 0x28),
            Color.FromRgb(0xD0, 0xD5, 0xDD),
            Color.FromRgb(0x17, 0x3A, 0x5E)),
    };

    public static string CreateDocumentBootstrapScript(DesktopThemePreference preference)
    {
        var wireValue = JsonSerializer.Serialize(ToWireValue(preference));
        return string.Concat(
            "(() => { const preference = ",
            wireValue,
            "; localStorage.setItem(\"",
            ThemePreferenceKey,
            "\", preference); const theme = preference === \"system\" ? (matchMedia(\"(prefers-color-scheme: dark)\").matches ? \"dark\" : \"light\") : preference; document.documentElement.dataset.theme = theme; document.documentElement.style.colorScheme = theme; })();");
    }

    public static void ApplyTitleBarTheme(Window window, DesktopThemePreference preference)
    {
        ArgumentNullException.ThrowIfNull(window);

        var windowHandle = new WindowInteropHelper(window).Handle;
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        var useDarkMode = Resolve(preference) == DesktopThemePreference.Dark ? 1 : 0;
        if (DwmSetWindowAttribute(windowHandle, 20, ref useDarkMode, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(windowHandle, 19, ref useDarkMode, sizeof(int));
        }
    }

    private static string PreferencePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SchedulerDesktop",
        PreferenceFileName);

    private sealed record StoredThemePreference(string Preference);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
