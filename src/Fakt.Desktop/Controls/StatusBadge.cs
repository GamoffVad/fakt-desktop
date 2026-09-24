using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace Fakt.Desktop.Controls
{
    /// <summary>
    /// Status label (DESIGN.md §6.6): text is always shown, the tone colours text and optional icon
    /// on the matching light background. Template: Themes/Controls/StatusBadge.xaml.
    /// </summary>
    public class StatusBadge : Control
    {
        private static readonly object BoxedFalse = false;

        static StatusBadge()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(StatusBadge), new FrameworkPropertyMetadata(typeof(StatusBadge)));
            FocusableProperty.OverrideMetadata(typeof(StatusBadge), new FrameworkPropertyMetadata(BoxedFalse));
            IsTabStopProperty.OverrideMetadata(typeof(StatusBadge), new FrameworkPropertyMetadata(BoxedFalse));
        }

        /// <summary>Badge text, e.g. «Табличный». Also used as the automation name.</summary>
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            "Text",
            typeof(string),
            typeof(StatusBadge),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure, OnTextChanged));

        public string Text
        {
            get { return (string)GetValue(TextProperty); }
            set { SetValue(TextProperty, value); }
        }

        /// <summary>Semantic tone that selects the Brush.Status.* colours.</summary>
        public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
            "Tone",
            typeof(BadgeTone),
            typeof(StatusBadge),
            new FrameworkPropertyMetadata(BadgeTone.Neutral, FrameworkPropertyMetadataOptions.AffectsRender));

        public BadgeTone Tone
        {
            get { return (BadgeTone)GetValue(ToneProperty); }
            set { SetValue(ToneProperty, value); }
        }

        /// <summary>Optional outline icon (24×24 geometry from Icons.xaml), rendered at 14 DIP.</summary>
        public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
            "Icon",
            typeof(Geometry),
            typeof(StatusBadge),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public Geometry Icon
        {
            get { return (Geometry)GetValue(IconProperty); }
            set { SetValue(IconProperty, value); }
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var badge = (StatusBadge)d;
            var text = (string)e.NewValue ?? string.Empty;

            // Mirror Text into AutomationProperties.Name unless the view set an explicit name.
            var source = DependencyPropertyHelper.GetValueSource(badge, AutomationProperties.NameProperty);
            if (source.BaseValueSource == BaseValueSource.Default || source.IsCurrent)
            {
                badge.SetCurrentValue(AutomationProperties.NameProperty, text);
            }

            var peer = UIElementAutomationPeer.FromElement(badge);
            if (peer != null)
            {
                peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, (string)e.OldValue ?? string.Empty, text);
            }
        }

        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new StatusBadgeAutomationPeer(this);
        }

        private sealed class StatusBadgeAutomationPeer : FrameworkElementAutomationPeer
        {
            public StatusBadgeAutomationPeer(StatusBadge owner)
                : base(owner)
            {
            }

            protected override string GetClassNameCore()
            {
                return "StatusBadge";
            }

            protected override AutomationControlType GetAutomationControlTypeCore()
            {
                return AutomationControlType.Text;
            }

            protected override string GetNameCore()
            {
                var name = base.GetNameCore();
                return string.IsNullOrEmpty(name) ? ((StatusBadge)Owner).Text ?? string.Empty : name;
            }

            protected override bool IsControlElementCore()
            {
                return true;
            }
        }
    }
}
