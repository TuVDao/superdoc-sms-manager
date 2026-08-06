using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace SuperDoc.Sms.WinUI;

/// <summary>
/// Makes the unpackaged build start with Windows.
/// </summary>
/// <remarks>
/// The MSIX build declares a <c>windows.startupTask</c> extension, but the packaged app cannot
/// receive SMS at all: a sideloaded unsigned package's <c>cellularMessaging</c> capability is
/// not honoured at runtime, and every registration attempt returns 0xD0000022. Only the
/// unpackaged build can receive - and an unpackaged app has no startup task, so it registers
/// itself under HKCU\...\Run instead.
///
/// HKCU rather than HKLM: no elevation, and the app is per-user anyway.
/// </remarks>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SUPERDOC SMS Manager";

    /// <summary>True when running with MSIX package identity, which manages startup itself.</summary>
    public static bool IsPackaged
    {
        get
        {
            try
            {
                _ = Windows.ApplicationModel.Package.Current;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Points the Run entry at the executable that is running right now, rewriting it if the
    /// app has been moved or rebuilt to a different path.
    /// </summary>
    public static void Enable(ILogger logger)
    {
        if (IsPackaged)
        {
            return;
        }

        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                logger.LogWarning("Cannot enable start-with-Windows: executable path is unknown.");
                return;
            }

            var command = $"\"{exePath}\" --autostart";

            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                logger.LogWarning("Cannot enable start-with-Windows: the Run key is unavailable.");
                return;
            }

            var current = key.GetValue(ValueName) as string;
            if (current == command)
            {
                return;
            }

            // Only claim the entry when there is none, or the one there points at an executable
            // that no longer exists. Otherwise running a development build out of bin\ would
            // silently hijack start-up from the installed copy in app\, and deleting bin\ later
            // would leave the user with no working auto-start at all.
            if (!string.IsNullOrEmpty(current) && RegisteredTargetExists(current))
            {
                logger.LogDebug("Leaving the existing start-with-Windows entry alone: {Command}", current);
                return;
            }

            key.SetValue(ValueName, command, RegistryValueKind.String);
            logger.LogInformation("Start-with-Windows enabled: {Command}", command);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not enable start-with-Windows.");
        }
    }

    /// <summary>Extracts the exe path out of a <c>"path" --autostart</c> command and tests it.</summary>
    private static bool RegisteredTargetExists(string command)
    {
        var trimmed = command.Trim();
        var path = trimmed.StartsWith('"')
            ? trimmed[1..(trimmed.IndexOf('"', 1) is var end && end > 0 ? end : trimmed.Length)]
            : trimmed.Split(' ')[0];

        return File.Exists(path);
    }

    public static void Disable(ILogger logger)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            logger.LogInformation("Start-with-Windows disabled.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not disable start-with-Windows.");
        }
    }
}
