using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Lemora.Controls
{
    /// <summary>
    /// Cornice delle finestre di servizio e dei dialoghi (blueprint §12): barra titolo con logo, titolo e chiusura, corpo, piè con i pulsanti.
    /// Lo stile e' in FluentIslands.xaml (chiave "DialogFrameStyle").
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
        /// <summary>contenuto a sinistra del piè (es. "Elimina cartella"), i pulsanti principali restano a destra</summary>
        public object? FooterLeft { get => GetValue(FooterLeftProperty); set => SetValue(FooterLeftProperty, value); }
        /// <summary>comando della ✕; se manca e la cornice sta in una finestra propria, la chiude</summary>
        public ICommand? CloseCommand { get => (ICommand?)GetValue(CloseCommandProperty); set => SetValue(CloseCommandProperty, value); }
        /// <summary>true nelle finestre vere: trascinando la barra titolo si sposta la finestra</summary>
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

    /// <summary>nasconde l'elemento quando il valore e' null (piè senza contenuto)</summary>
    public class NullToCollapsedConverter : System.Windows.Data.IValueConverter
    {
        public static readonly NullToCollapsedConverter Instance = new();
        public object Convert(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value == null ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new System.NotSupportedException();
    }

    public enum DialogIconKind { Info, Question, Warning, Error }

    /// <summary>icona tonda dei dialoghi: ? ! i sull'accento, ✕ sul rosso tenue</summary>
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
