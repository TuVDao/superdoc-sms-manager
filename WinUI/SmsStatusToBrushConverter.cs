using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using SuperDoc.Sms.Models;
using Windows.UI;

namespace SuperDoc.Sms.WinUI;

public sealed class SmsStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var status = value is SmsStatus s ? s : SmsStatus.Pending;
        return status switch
        {
            SmsStatus.Sent => new SolidColorBrush(Color.FromArgb(255, 22, 163, 74)),
            SmsStatus.Failed => new SolidColorBrush(Color.FromArgb(255, 220, 38, 38)),
            SmsStatus.Sending => new SolidColorBrush(Color.FromArgb(255, 37, 99, 235)),
            SmsStatus.Received => new SolidColorBrush(Color.FromArgb(255, 124, 58, 237)),
            _ => new SolidColorBrush(Color.FromArgb(255, 245, 158, 11))
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
