using Microsoft.Extensions.Logging;

namespace MyApp.Logging;

public static class LoggingSetup
{
    public static ILoggerFactory CreateLoggerFactory(string? baseDir = null)
    {
        var root = baseDir
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Message_T480s");

        // The console harness, the unpackaged build and the packaged build can all be open at
        // once. Appending them to one shared file interleaves and truncates each other's lines,
        // which corrupts exactly the stack traces needed to diagnose a crash - so give each
        // process its own file.
        var pid = Environment.ProcessId;
        var logPath = Path.Combine(root, "Logs", $"smsmanager-{DateTime.UtcNow:yyyyMMdd}-{pid}.log");

        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
            builder.AddProvider(new FileLoggerProvider(logPath));
        });
    }
}
