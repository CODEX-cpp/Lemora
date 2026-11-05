using System;
using System.Windows;
using Shawn.Utils.WpfResources.Theme.Styles;

namespace Lemora.View.Utils
{
    /// <summary>finestra della password principale: Create = primo avvio, Unlock = sblocco a mano, Change = nuova password dalle Impostazioni</summary>
    public partial class VaultWindow : WindowChromeBase
    {
        public enum Mode { Create, Unlock, Change }

        private const int MinLength = 8;
        private readonly Mode _mode;
        private readonly Func<string, bool>? _check;

        public string Password { get; private set; } = "";
        public bool RememberWithHello => CbHello.Visibility == Visibility.Visible && CbHello.IsChecked == true;

        /// <param name="check">solo per Unlock: true se la password apre la cassaforte (altrimenti la finestra resta aperta e lo dice)</param>
        public VaultWindow(Mode mode, bool helloAvailable, Func<string, bool>? check = null)
        {
            InitializeComponent();
            _mode = mode;
            _check = check;
            TbTitle.Text = IoC.Translate(mode switch { Mode.Create => "vault_create_title", Mode.Change => "vault_change_title", _ => "vault_unlock_title" });
            TbInfo.Text = IoC.Translate(mode switch { Mode.Create => "vault_create_info", Mode.Change => "vault_change_info", _ => "vault_unlock_info" });
            TbLabel1.Text = IoC.Translate(mode == Mode.Unlock ? "vault_password" : "vault_new_password");
            TbLabel2.Text = IoC.Translate("vault_confirm_password");
            PanelConfirm.Visibility = mode == Mode.Unlock ? Visibility.Collapsed : Visibility.Visible;
            TbHello.Text = IoC.Translate("vault_remember_hello");
            CbHello.Visibility = helloAvailable && mode != Mode.Change ? Visibility.Visible : Visibility.Collapsed;
            BtnOk.Content = IoC.Translate(mode == Mode.Unlock ? "vault_unlock" : "vault_save");
            BtnCancel.Content = IoC.Translate(mode == Mode.Change ? "vault_cancel" : "vault_exit");
            Loaded += (_, _) => { Activate(); Pwd1.Focus(); };
        }

        private void ShowError(string key)
        {
            TbError.Text = IoC.Translate(key);
            TbError.Visibility = Visibility.Visible;
        }

        private void BtnOk_OnClick(object sender, RoutedEventArgs e)
        {
            var p = Pwd1.Password;
            if (_mode == Mode.Unlock)
            {
                if (p.Length == 0 || _check?.Invoke(p) != true) { ShowError("vault_wrong_password"); Pwd1.Clear(); Pwd1.Focus(); return; }
            }
            else
            {
                if (p.Length < MinLength) { ShowError("vault_too_short"); return; }
                if (p != Pwd2.Password) { ShowError("vault_mismatch"); return; }
            }
            Password = p;
            DialogResult = true;
        }

        private void BtnCancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
