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

/// <summary>Bubble colour: neutral grey for what arrived, blue for what we sent.</summary>
public sealed class IncomingToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool incoming && incoming
            ? new SolidColorBrush(Color.FromArgb(255, 58, 58, 60))
            : new SolidColorBrush(Color.FromArgb(255, 37, 99, 235));

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
                SmsStatus.Failed => Strings.Current.StatusFailed,
                _ => string.Empty
            };

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
