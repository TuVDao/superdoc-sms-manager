using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using SuperDoc.Sms.Models;

namespace SuperDoc.Sms.WinUI;

public sealed class SmsStatusToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var status = value is SmsStatus s ? s : SmsStatus.Pending;

        if (parameter is string param)
        {
            if (string.Equals(param, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                return status == SmsStatus.Pending ? Visibility.Visible : Visibility.Collapsed;
            }
            if (string.Equals(param, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                return status == SmsStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        return status == SmsStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
