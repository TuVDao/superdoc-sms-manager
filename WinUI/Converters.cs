using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using SuperDoc.Sms.Models;
using Windows.UI;

namespace SuperDoc.Sms.WinUI;

/// <summary>Shows an element when the bound bool is true; pass "Invert" to flip it.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Bold when the bound bool says the item is unread, normal weight otherwise.
/// </summary>
/// <remarks>
/// Pass "SemiBold" as the parameter where the read state should still sit above body text — the
/// thread list titles are already SemiBold, so plain Normal would make a read thread look
/// demoted rather than simply read.
/// </remarks>
public sealed class UnreadToFontWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool unread && unread)
        {
            return FontWeights.Bold;
        }

        return parameter is string s && s.Equals("SemiBold", StringComparison.OrdinalIgnoreCase)
            ? FontWeights.SemiBold
            : FontWeights.Normal;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>Shows an element when the bound string has content.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Incoming messages sit on the left, outgoing on the right - the arrangement that makes a
/// thread readable at a glance.
/// </summary>
public sealed class IncomingToAlignmentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool incoming && incoming ? HorizontalAlignment.Left : HorizontalAlignment.Right;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>Bubble colour: neutral card background for what arrived, accent color for what we sent.</summary>
public sealed class IncomingToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool incoming && incoming)
        {
            if (Application.Current is { } app && app.Resources.TryGetValue("CardBackgroundFillColorSecondaryBrush", out var cardBrush) && cardBrush is Brush cb)
            {
                return cb;
            }

            return new SolidColorBrush(Color.FromArgb(255, 48, 48, 52));
        }

        if (Application.Current is { } app2 && app2.Resources.TryGetValue("AccentFillColorDefaultBrush", out var brush) && brush is Brush b)
        {
            return b;
        }

        return new SolidColorBrush(Color.FromArgb(255, 0, 120, 212));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>Border stroke: subtle card stroke for incoming bubbles, transparent for outbound accent bubbles.</summary>
public sealed class IncomingToBorderBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool incoming && incoming)
        {
            if (Application.Current is { } app && app.Resources.TryGetValue("CardStrokeColorDefaultBrush", out var brush) && brush is Brush b)
            {
                return b;
            }

            return new SolidColorBrush(Color.FromArgb(32, 255, 255, 255));
        }

        return new SolidColorBrush(Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Text contrast & hierarchy: On incoming bubbles, uses ThemeResource TextFillColorPrimary/Secondary.
/// On outgoing accent bubbles, uses ThemeResource TextOnAccentFillColorPrimary/Secondary.
/// Pass parameter "Secondary" for timestamps and status indicators.
/// </summary>
public sealed class IncomingToForegroundBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var incoming = value is bool b && b;
        var isSecondary = parameter is string s && s.Equals("Secondary", StringComparison.OrdinalIgnoreCase);

        if (incoming)
        {
            var key = isSecondary ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush";
            if (Application.Current is { } app && app.Resources.TryGetValue(key, out var brush) && brush is Brush resBrush)
            {
                return resBrush;
            }

            return new SolidColorBrush(isSecondary ? Color.FromArgb(255, 156, 163, 175) : Colors.White);
        }
        else
        {
            var key = isSecondary ? "TextOnAccentFillColorSecondaryBrush" : "TextOnAccentFillColorPrimaryBrush";
            if (Application.Current is { } app && app.Resources.TryGetValue(key, out var brush) && brush is Brush resBrush)
            {
                return resBrush;
            }

            return new SolidColorBrush(isSecondary ? Color.FromArgb(204, 255, 255, 255) : Colors.White);
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// The per-message status line inside a bubble. Received messages need no status - they are
/// self-evidently delivered - so only outbound states produce text.
/// </summary>
public sealed class SmsStatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is not SmsStatus status
            ? string.Empty
            : status switch
            {
                SmsStatus.Pending => Strings.Current.StatusPending,
                SmsStatus.Sending => Strings.Current.StatusSending,
                SmsStatus.Sent => Strings.Current.StatusSent,
                SmsStatus.Delivered => Strings.Current.StatusDelivered,
                SmsStatus.Failed => Strings.Current.StatusFailed,
                _ => string.Empty
            };

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>Active tab styling: subtle rounded background when true (Windows 11 NavigationView style), transparent when false.</summary>
public sealed class BoolToNavBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isActive = value is bool b && b;
        if (!isActive)
        {
            return new SolidColorBrush(Colors.Transparent);
        }

        if (Application.Current is { } app && app.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out var brush) && brush is Brush navBrush)
        {
            return navBrush;
        }

        return new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>Active tab styling: primary text when active, secondary text when inactive (adapts to light and dark themes).</summary>
public sealed class BoolToNavForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isActive = value is bool b && b;
        var key = isActive ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush";
        if (Application.Current is { } app && app.Resources.TryGetValue(key, out var brush) && brush is Brush navBrush)
        {
            return navBrush;
        }

        return new SolidColorBrush(isActive ? Colors.White : Color.FromArgb(255, 156, 163, 175));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

public sealed class BoolToModemBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isReady = value is bool b && b;
        return isReady
            ? new SolidColorBrush(Color.FromArgb(255, 34, 197, 94))
            : new SolidColorBrush(Color.FromArgb(255, 156, 163, 175));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
