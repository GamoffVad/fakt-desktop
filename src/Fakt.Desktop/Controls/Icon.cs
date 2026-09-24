using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Fakt.Desktop.Controls
{
    /// <summary>
    /// Vector icon from Themes/Icons.xaml. The geometry is drawn in a 24×24 coordinate space and
    /// scaled to <see cref="Size"/>; by default it is stroked (outline style, 2 units, round caps and joins)
    /// with <see cref="Control.Foreground"/>, which is inherited from the parent (button text colour etc.).
    /// </summary>
    /// <remarks>
    /// The template lives in Themes/Icons.xaml (implicit style "Icon.Default"), not in Themes/Generic.xaml.
    /// Icons are decorative: when an icon is the only content of a control, give that control a ToolTip
    /// and AutomationProperties.Name.
    /// </remarks>
    public class Icon : Control
    {
        private static readonly object BoxedFalse = false;

        static Icon()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(typeof(Icon)));
            FocusableProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(BoxedFalse));
            IsTabStopProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(BoxedFalse));
        }

        /// <summary>Icon geometry in 24×24 space (e.g. <c>{StaticResource Icon.Folder}</c>).</summary>
        public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
            "Data",
            typeof(Geometry),
            typeof(Icon),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public Geometry Data
        {
            get { return (Geometry)GetValue(DataProperty); }
            set { SetValue(DataProperty, value); }
        }

        /// <summary>Rendered width and height in DIP (default 20 = Size.Icon.Default).</summary>
        public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
            "Size",
            typeof(double),
            typeof(Icon),
            new FrameworkPropertyMetadata(20.0, FrameworkPropertyMetadataOptions.AffectsMeasure),
            IsValidSize);

        public double Size
        {
            get { return (double)GetValue(SizeProperty); }
            set { SetValue(SizeProperty, value); }
        }

        /// <summary>
        /// When <c>true</c> the geometry is filled with Foreground and not stroked
        /// (used for filled marks such as <c>Icon.BrandMark</c>).
        /// </summary>
        public static readonly DependencyProperty IsFilledProperty = DependencyProperty.Register(
            "IsFilled",
            typeof(bool),
            typeof(Icon),
            new FrameworkPropertyMetadata(BoxedFalse, FrameworkPropertyMetadataOptions.AffectsRender));

        public bool IsFilled
        {
            get { return (bool)GetValue(IsFilledProperty); }
            set { SetValue(IsFilledProperty, value); }
        }

        private static bool IsValidSize(object value)
        {
            var size = (double)value;
            return !double.IsNaN(size) && !double.IsInfinity(size) && size >= 0.0;
        }
    }
}
