namespace SuperDoc.Sms.Services;

/// <summary>
/// Runs the interface against a throwaway database with the modem left alone.
/// </summary>
/// <remarks>
/// Two things need this. Documentation screenshots must not show a real person's number, and
/// anyone working on the interface without a WWAN module — most contributors — otherwise gets a
/// window that can only report "no modem".
///
/// The modem is deliberately never opened in this mode. A second process that registered for
/// incoming SMS would compete with the copy the user actually relies on, and a screenshot is
/// not worth dropping someone's messages for.
/// </remarks>
public static class DemoMode
{
    /// <summary>Set to <c>1</c> to enable.</summary>
    public const string EnvironmentVariable = "SUPERDOC_SMS_DEMO";

    /// <summary>Overrides the database location; defaults to a file beside the real one.</summary>
    public const string DatabaseEnvironmentVariable = "SUPERDOC_SMS_DB";

    private static readonly Lazy<bool> Enabled = new(() =>
        string.Equals(
            Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim(),
            "1",
            StringComparison.Ordinal));

    public static bool IsEnabled => Enabled.Value;

    /// <summary>
    /// The database to open, or null to use the normal per-user location.
    /// </summary>
    public static string? DatabasePathOverride
    {
        get
        {
            var explicitPath = Environment.GetEnvironmentVariable(DatabaseEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                return explicitPath.Trim();
            }

            if (!IsEnabled)
            {
                return null;
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "smsmanager-demo.db");
        }
    }
}
