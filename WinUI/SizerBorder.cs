using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Message_T480s.WinUI;

/// <summary>
/// A drag handle that shows a resize cursor.
/// </summary>
/// <remarks>
/// This exists only because <c>UIElement.ProtectedCursor</c> is protected: a plain element in
/// XAML cannot have its cursor changed from the code-behind, so the handle has to be a type of
/// our own. It derives from <see cref="Grid"/> rather than Border because Border is sealed in
/// WinUI. Kept deliberately tiny - the drag logic itself lives in <c>MainWindow.xaml.cs</c>, and
/// the Community Toolkit sizer controls cannot be used here because they break XAML compilation
/// (see README).
/// </remarks>
public partial class SizerBorder : Grid
{
    private bool _isHorizontalDrag = true;

    public SizerBorder()
    {
        ApplyCursor();
    }

    /// <summary>
    /// True for a vertical bar that changes a width (east-west cursor); false for a horizontal
    /// bar that changes a height (north-south cursor).
    /// </summary>
    public bool IsHorizontalDrag
    {
        get => _isHorizontalDrag;
        set
        {
            _isHorizontalDrag = value;
            ApplyCursor();
        }
    }

    private void ApplyCursor()
    {
        try
        {
            ProtectedCursor = InputSystemCursor.Create(
                _isHorizontalDrag ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.SizeNorthSouth);
        }
        catch (Exception)
        {
            // A missing cursor is cosmetic; dragging still works without it.
        }
    }
}
