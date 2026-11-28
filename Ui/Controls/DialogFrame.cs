using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Lemora.Controls
{
    /// <summary>
    /// Frame for utility windows and dialogs (blueprint §12): title bar with logo, title and close button, body, footer with the buttons.
    /// The style is in FluentIslands.xaml (key "DialogFrameStyle").
    /// </summary>
    public class DialogFrame : ContentControl
    {
        public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(DialogFrame), new PropertyMetadata(""));
        public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(nameof(Footer), typeof(object), typeof(DialogFrame), new PropertyMetadata(null));
        public static readonly DependencyProperty FooterLeftProperty = DependencyProperty.Register(nameof(FooterLeft), typeof(object), typeof(DialogFrame), new PropertyMetadata(null));
        public static readonly DependencyProperty CloseCommandProperty = DependencyProperty.Register(nameof(CloseCommand), typeof(ICommand), typeof(DialogFrame), new PropertyMetadata(null));
        public static readonly DependencyProperty CanDragWindowProperty = DependencyProperty.Register(nameof(CanDragWindow), typeof(bool), typeof(DialogFrame), new PropertyMetadata(false));
        public static readonly DependencyProperty ShowCloseProperty = DependencyProperty.Register(nameof(ShowClose), typeof(bool), typeof(DialogFrame), new PropertyMetadata(true));

        static DialogFrame()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(DialogFrame), new FrameworkPropertyMetadata(typeof(DialogFrame)));
        }

        public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
        public object? Footer { get => GetValue(FooterProperty); set => SetValue(FooterProperty, value); }
        /// <summary>content on the left of the footer (e.g. "Delete folder"); the main buttons stay on the right</summary>
        public object? FooterLeft { get => GetValue(FooterLeftProperty); set => SetValue(FooterLeftProperty, value); }
        /// <summary>command of the ✕; if missing and the frame is in its own window, it closes that window</summary>
        public ICommand? CloseCommand { get => (ICommand?)GetValue(CloseCommandProperty); set => SetValue(CloseCommandProperty, value); }
        /// <summary>true in real windows: dragging the title bar moves the window</summary>
        public bool CanDragWindow { get => (bool)GetValue(CanDragWindowProperty); set => SetValue(CanDragWindowProperty, value); }
        public bool ShowClose { get => (bool)GetValue(ShowCloseProperty); set => SetValue(ShowCloseProperty, value); }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            if (GetTemplateChild("PART_Close") is Button close)
            {
                close.Visibility = ShowClose ? Visibility.Visible : Visibility.Collapsed;
                close.Click += (_, _) =>
                {
                    if (CloseCommand?.CanExecute(null) == true) CloseCommand.Execute(null);
                    else if (CloseCommand == null && CanDragWindow) Window.GetWindow(this)?.Close();
                };
            }
            if (GetTemplateChild("PART_TitleBar") is UIElement bar)
                bar.MouseLeftButtonDown += (_, e) =>
                {
                    if (CanDragWindow && e.ButtonState == MouseButtonState.Pressed) Window.GetWindow(this)?.DragMove();
                };
        }
    }

    /// <summary>hides the element when the value is null (footer without content)</summary>
    public class NullToCollapsedConverter : System.Windows.Data.IValueConverter
    {
        public static readonly NullToCollapsedConverter Instance = new();
        public object Convert(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value == null ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new System.NotSupportedException();
    }

    public enum DialogIconKind { Info, Question, Warning, Error }

    /// <summary>round dialog icon: ? ! i on the accent color, ✕ on soft red</summary>
    public class DialogIcon : Border
    {
        public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(DialogIconKind), typeof(DialogIcon),
            new PropertyMetadata(DialogIconKind.Info, (d, _) => ((DialogIcon)d).Rebuild()));

        public DialogIconKind Kind { get => (DialogIconKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

        public DialogIcon()
        {
            Width = Height = 44;
            CornerRadius = new CornerRadius(22);
            VerticalAlignment = VerticalAlignment.Top;
            Rebuild();
        }

        private void Rebuild()
        {
            var isError = Kind == DialogIconKind.Error;
            var glyph = Kind switch { DialogIconKind.Question => "?", DialogIconKind.Warning => "!", DialogIconKind.Error => "✕", _ => "i" };
            if (isError)
            {
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x9B, 0x94));
            }
            else
            {
                SetResourceReference(BackgroundProperty, "AccentMidBrush");
            }
            var text = new TextBlock
            {
                Text = glyph,
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (isError) text.Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x0B, 0x08));
            else text.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
            Child = text;
        }
    }
}
