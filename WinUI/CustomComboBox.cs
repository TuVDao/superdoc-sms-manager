using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace SuperDoc.Sms.WinUI;

/// <summary>
/// A WinUI 3 ComboBox that forces its dropdown popup to open directly BELOW the control
/// instead of centering on the selected item and overflowing upward over the button and title bar.
/// </summary>
public class CustomComboBox : ComboBox
{
    private Popup? _popup;

    public CustomComboBox()
    {
        Loaded += OnLoaded;
        DropDownOpened += OnDropDownOpened;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        HookPopup();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HookPopup();
    }

    private void OnDropDownOpened(object? sender, object e)
    {
        HookPopup();
        ApplyBottomPlacement();
    }

    private void HookPopup()
    {
        if (_popup == null)
        {
            _popup = GetTemplateChild("Popup") as Popup
                     ?? FindVisualChild<Popup>(this);

            if (_popup != null)
            {
                _popup.Opened += OnPopupOpened;
                ApplyBottomPlacement();
            }
        }
    }

    private void OnPopupOpened(object? sender, object e)
    {
        ApplyBottomPlacement();
    }

    private void ApplyBottomPlacement()
    {
        if (_popup == null)
        {
            _popup = GetTemplateChild("Popup") as Popup
                     ?? FindVisualChild<Popup>(this);
        }

        if (_popup != null)
        {
            _popup.PlacementTarget = this;
            _popup.DesiredPlacement = PopupPlacementMode.BottomEdgeAlignedLeft;
            _popup.VerticalOffset = 2;
            _popup.HorizontalOffset = 0;
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            var nested = FindVisualChild<T>(child);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }
}
