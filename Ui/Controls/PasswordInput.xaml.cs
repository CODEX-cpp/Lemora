using System.Windows;
using System.Windows.Controls;
using System.Reflection;
using Lemora.Service;
using Stylet;

namespace Lemora.Controls
{
    /// <summary>
    /// Interaction logic for PasswordInput.xaml
    /// </summary>
    public partial class PasswordInput : UserControl
    {
        public PasswordInput()
        {
            InitializeComponent();
            CipherTextBox.PasswordChanged += PasswordBox_PasswordChanged;
            PlainTextBox.TextChanged += PlainTextBoxOnPlainTextBoxChanged;
            PlainTextBox.Visibility = Visibility.Collapsed;
        }

        public static readonly DependencyProperty PasswordProperty =
            DependencyProperty.Register("Password", typeof(string), typeof(PasswordInput),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, new PropertyChangedCallback(OnServerDataChanged)));

        public new void Focus()
        {
            CipherTextBox.Focus();
        }

        private static void OnServerDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PasswordInput p && e.NewValue is string s && p.CipherTextBox.Password != s)
            {
                // empty, or identical to a password typed by hand in this session (the tab can be recreated, e.g. protocol change while creating):
                // it can be shown again freely. A password coming from a saved entry requires verification to be shown
                p._typedHere = s.Length == 0 || SessionTyped.Contains(s);
                p.CipherTextBox.PasswordChanged -= p.PasswordBox_PasswordChanged;
                p.PlainTextBox.TextChanged -= p.PlainTextBoxOnPlainTextBoxChanged;
                p.PlainTextBox.Text = s;
                p.CipherTextBox.Password = s;
                p.CipherTextBox.PasswordChanged += p.PasswordBox_PasswordChanged;
                p.PlainTextBox.TextChanged += p.PlainTextBoxOnPlainTextBoxChanged;
                // value set from outside (another credential in the same field): an eye toggle left open must not show it without verification
                // (security hardening)
                if (!p._typedHere)
                {
                    p.PlainTextBox.Visibility = Visibility.Hidden;
                    p.EyeToggle.Unchecked -= p.ToggleButton_Unchecked;
                    p.EyeToggle.IsChecked = false;
                    p.EyeToggle.Unchecked += p.ToggleButton_Unchecked;
                }
            }
        }
        public string Password
        {
            get => (string)GetValue(PasswordProperty);
            set => SetValue(PasswordProperty, value);
        }

        /// <summary>passwords typed by hand in this app session (memory only): showing them again does not require verification</summary>
        private static readonly System.Collections.Generic.HashSet<string> SessionTyped = new System.Collections.Generic.HashSet<string>();

        /// <summary>call when the editor closes or another one starts: from then on every saved password requires verification to be shown</summary>
        public static void ForgetTyped() => SessionTyped.Clear();

        /// <summary>called on every edit made by the user: if the password was cleared and retyped from scratch it is entirely theirs (free to show again);
        /// if instead it starts from the saved one (adding or removing a character) it stays protected, otherwise they could read the part they did not write</summary>
        private void NoteUserEdit(string value)
        {
            if (value.Length == 0) _typedHere = true;
            if (_typedHere && value.Length > 0) SessionTyped.Add(value);
        }

        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            PlainTextBox.TextChanged -= PlainTextBoxOnPlainTextBoxChanged;
            PlainTextBox.Text = CipherTextBox.Password;
            PlainTextBox.TextChanged += PlainTextBoxOnPlainTextBoxChanged;
            NoteUserEdit(CipherTextBox.Password);
            Password = CipherTextBox.Password;
        }

        private void PlainTextBoxOnPlainTextBoxChanged(object sender, TextChangedEventArgs e)
        {
            CipherTextBox.PasswordChanged -= PasswordBox_PasswordChanged;
            CipherTextBox.Password = PlainTextBox.Text;
            CipherTextBox.PasswordChanged += PasswordBox_PasswordChanged;
            NoteUserEdit(CipherTextBox.Password);
            Password = CipherTextBox.Password;
        }

        // true if the password was typed here from scratch (new entry): whoever typed it need not verify again to show it
        private bool _typedHere = true;

        private void ToggleButton_Checked(object sender, RoutedEventArgs e)
        {
            if (_typedHere)
            {
                PlainTextBox.Visibility = Visibility.Visible;
                PlainTextBox.Focus();
                PlainTextBox.CaretIndex = PlainTextBox.Text.Length;
                return;
            }
            SecondaryVerificationHelper.VerifyAsyncUiCallBack(b =>
                {
                    Execute.OnUIThreadSync(() =>
                    {

                        if (b != true)
                        {
                            if (sender is CheckBox cb)
                                cb.IsChecked = false;
                            return;
                        }
                        PlainTextBox.Visibility = Visibility.Visible;
                        PlainTextBox.Focus();
                        PlainTextBox.CaretIndex = PlainTextBox.Text.Length;
                    });
                });
        }
        private void SetSelection(PasswordBox passwordBox, int start, int length = 0)
        {
            if (start < 0) start = 0;
            if (length < 0) length = 0;
            passwordBox.GetType().GetMethod("Select", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(passwordBox, new object[] { start, length });
        }
        private void ToggleButton_Unchecked(object sender, RoutedEventArgs e)
        {
            CipherTextBox.Focus();
            SetSelection(CipherTextBox, CipherTextBox.Password.Length);
            PlainTextBox.Visibility = Visibility.Hidden;
        }




        public int CaretIndex
        {
            set
            {
                PlainTextBox.CaretIndex = value;
                SetSelection(CipherTextBox, value);
            }
        }
    }
}
