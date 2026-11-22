using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using SuperDoc.Sms.Models;
using System.Net;

namespace SuperDoc.Sms.WinUI;

/// <summary>
/// Shows a Windows notification for each inbound SMS. Without this, a message arriving while
/// the window is hidden in the tray would be silent and the app would look like it did nothing.
/// </summary>
public sealed class ToastNotifier : IDisposable
{
    private readonly ILogger _logger;
    private readonly Strings _loc;
    private bool _registered;
    private bool _disposed;

    /// <summary>Raised when the user submits an inline reply from a Windows toast banner.</summary>
    public event Action<string, string>? ReplyRequested;

    public ToastNotifier(ILogger logger, Strings loc)
    {
        _logger = logger;
        _loc = loc;

        try
        {
            // The parameterless overload registers the AUMID with only a NotificationGUID and no
            // DisplayName. Windows then accepts Show() without error and silently drops the
            // banner: with no name to attribute it to, the app never even appears under
            // Settings > Notifications. Passing a display name and icon is what makes an
            // unpackaged app's toasts actually appear on screen.
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.png");
            var icon = File.Exists(iconPath) ? new Uri(iconPath) : null;

            if (icon is null)
            {
                _logger.LogWarning("Notification icon missing at {Path}.", iconPath);
            }

            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register("SUPERDOC SMS Manager", icon);
            _registered = true;

            // Logged at Information deliberately: whether notifications actually work was
            // previously unknowable without watching the screen at the right moment.
            _logger.LogInformation("Toast notifications registered.");
        }
        catch (Exception ex)
        {
            // Unpackaged runs without a registered COM activator land here. Not fatal: the
            // message is still stored and shown in the list.
            _logger.LogWarning(ex, "Toast notifications unavailable; incoming SMS will not raise a banner.");
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        try
        {
            if (args.Arguments.TryGetValue("action", out var action) && action == "reply")
            {
                if (args.Arguments.TryGetValue("peer", out var peer) &&
                    args.UserInput.TryGetValue("replyBox", out var replyText))
                {
                    if (!string.IsNullOrWhiteSpace(replyText))
                    {
                        _logger.LogInformation("Inline toast reply received for {Peer}.", peer);
                        ReplyRequested?.Invoke(peer, replyText);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process notification activation.");
        }
    }

    /// <summary>True when Windows accepted the notification registration.</summary>
    public bool IsAvailable => _registered;

    /// <summary>Shows a notification on demand, so the feature can be checked without waiting
    /// for someone to send a real message.</summary>
    public void ShowTest()
    {
        if (!_registered)
        {
            _logger.LogWarning("Test notification skipped: notifications are not registered.");
            return;
        }

        try
        {
            var toast = new AppNotificationBuilder()
                .AddText("SUPERDOC SMS Manager")
                .AddText(_loc.TestNotificationBody)
                .BuildNotification();

            AppNotificationManager.Default.Show(toast);
            _logger.LogInformation("Test notification shown.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Test notification failed.");
        }
    }

    public void ShowIncoming(SmsMessage message)
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            var from = string.IsNullOrWhiteSpace(message.From) ? _loc.UnknownNumber : message.From;
            var body = message.Body.Length > 200 ? message.Body[..200] + "..." : message.Body;

            // The builder writes XML, so anything coming off the air has to be escaped.
            var builder = new AppNotificationBuilder()
                .AddText(WebUtility.HtmlEncode(from))
                .AddText(WebUtility.HtmlEncode(body));

            // Only offer quick reply for numbers that can receive an SMS back (exclude shortcodes & alphanumeric senders)
            var canReply = !string.IsNullOrWhiteSpace(message.From) &&
                           !message.From.Any(char.IsLetter) &&
                           message.From.Count(char.IsDigit) >= 6;

            if (canReply)
            {
                builder.AddTextBox("replyBox", "Type a reply...", "Reply")
                       .AddButton(new AppNotificationButton("Send")
                           .AddArgument("action", "reply")
                           .AddArgument("peer", message.From)
                           .SetInputId("replyBox"));
            }

            var toast = builder.BuildNotification();

            AppNotificationManager.Default.Show(toast);

            // Leaves proof in the log that the banner was raised for this message.
            _logger.LogInformation("Notification shown for incoming SMS #{Id}.", message.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not show notification for SMS #{Id}.", message.Id);
        }
    }

    public void Dispose()
    {
        if (_disposed || !_registered)
        {
            return;
        }

        _disposed = true;

        try
        {
            AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignoring failure while unregistering notifications at shutdown.");
        }
    }
}
