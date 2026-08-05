using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using MyApp.Models;

namespace Message_T480s.WinUI;

public sealed class SmsStatusToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var status = value is SmsStatus s ? s : SmsStatus.Pending;
        return status == SmsStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
