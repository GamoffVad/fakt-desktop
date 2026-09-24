using System.Windows;
using System.Windows.Media;

namespace Fakt.Desktop.Controls
{
    /// <summary>
    /// Attached properties used by the Arctic Blue control templates (Themes/Controls/*.xaml).
    /// </summary>
    /// <remarks>
    /// Usage in XAML: <c>xmlns:ui="clr-namespace:Fakt.Desktop.Controls"</c>, then e.g.
    /// <c>&lt;Button Style="{StaticResource Button.Primary}" ui:Ui.Icon="{StaticResource Icon.Folder}" Content="Выбрать папку"/&gt;</c>.
    /// </remarks>
    public static class Ui
    {
        private static readonly object BoxedFalse = false;

        #region Icon

        /// <summary>
        /// Outline icon geometry (24×24 coordinate space, see Themes/Icons.xaml) shown by buttons,
        /// navigation items and other templates that support an icon slot.
        /// </summary>
        public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
            "Icon",
            typeof(Geometry),
            typeof(Ui),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static Geometry GetIcon(DependencyObject element)
        {
            return (Geometry)element.GetValue(IconProperty);
        }

        public static void SetIcon(DependencyObject element, Geometry value)
        {
            element.SetValue(IconProperty, value);
        }

        #endregion

        #region IsBusy

        /// <summary>
        /// When <c>true</c>, buttons replace their icon with an indeterminate spinner of the same size.
        /// Re-execution must still be blocked by the command (CanExecute = false), not by the theme.
        /// </summary>
        public static readonly DependencyProperty IsBusyProperty = DependencyProperty.RegisterAttached(
            "IsBusy",
            typeof(bool),
            typeof(Ui),
            new FrameworkPropertyMetadata(BoxedFalse));

        public static bool GetIsBusy(DependencyObject element)
        {
            return (bool)element.GetValue(IsBusyProperty);
        }

        public static void SetIsBusy(DependencyObject element, bool value)
        {
            element.SetValue(IsBusyProperty, value);
        }

        #endregion

        #region HasError

        /// <summary>
        /// Error state for inputs: red border (keyboard focus ring stays visible on top).
        /// The explanatory text must be shown next to / under the field by the view (Text.Error style).
        /// </summary>
        public static readonly DependencyProperty HasErrorProperty = DependencyProperty.RegisterAttached(
            "HasError",
            typeof(bool),
            typeof(Ui),
            new FrameworkPropertyMetadata(BoxedFalse));

        public static bool GetHasError(DependencyObject element)
        {
            return (bool)element.GetValue(HasErrorProperty);
        }

        public static void SetHasError(DependencyObject element, bool value)
        {
            element.SetValue(HasErrorProperty, value);
        }

        #endregion

        #region IsCompact

        /// <summary>
        /// Inherited flag for the compact (64 DIP) navigation rail: navigation items hide their
        /// label, centre the icon and show the label as a tooltip. Set it on the navigation container.
        /// </summary>
        public static readonly DependencyProperty IsCompactProperty = DependencyProperty.RegisterAttached(
            "IsCompact",
            typeof(bool),
            typeof(Ui),
            new FrameworkPropertyMetadata(BoxedFalse, FrameworkPropertyMetadataOptions.Inherits));

        public static bool GetIsCompact(DependencyObject element)
        {
            return (bool)element.GetValue(IsCompactProperty);
        }

        public static void SetIsCompact(DependencyObject element, bool value)
        {
            element.SetValue(IsCompactProperty, value);
        }

        #endregion

        #region Placeholder

        /// <summary>
        /// Optional watermark shown by TextBox / ComboBox templates while the value is empty.
        /// A placeholder never replaces the permanent label above the field (DESIGN.md §6.2).
        /// </summary>
        public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
            "Placeholder",
            typeof(string),
            typeof(Ui),
            new FrameworkPropertyMetadata(null));

        public static string GetPlaceholder(DependencyObject element)
        {
            return (string)element.GetValue(PlaceholderProperty);
        }

        public static void SetPlaceholder(DependencyObject element, string value)
        {
            element.SetValue(PlaceholderProperty, value);
        }

        #endregion
    }
}
