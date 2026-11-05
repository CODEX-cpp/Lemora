using System;
using Lemora.Service.Locality;
using Lemora.View.Utils.MaskAndPop;
using Shawn.Utils.Wpf;

namespace Lemora.View.Editor
{
    public class PasswordPopupDialogViewModel : PopupBase
    {
        private string _title = "";
        public string Title
        {
            get => _title;
            set
            {
                if (SetAndNotify(ref _title, value))
                {
                    if (View is PasswordPopupDialogView v)
                    {
                        v.Title = value;
                    }
                }
            }
        }

        // Utente = (dominio della cartella, mostrato come chip che si puo' togliere) + testo scritto. Senza chip o con "dominio\utente" gia' scritto
        // il nome resta quello digitato: scrivendo solo "administrator" si usa l'utente locale.
        private string _userNameInput = "";
        private string _domain = "";
        private bool _domainRemoved;

        /// <summary>dominio proposto dalla cartella ("" = nessuno)</summary>
        public string Domain
        {
            get => _domain;
            set
            {
                if (SetAndNotifyIfChanged(ref _domain, value ?? ""))
                {
                    RaisePropertyChanged(nameof(HasDomain)); RaisePropertyChanged(nameof(DomainChip)); RaisePropertyChanged(nameof(UserName));
                }
            }
        }
        public bool HasDomain => !string.IsNullOrEmpty(_domain) && !_domainRemoved;
        public string DomainChip => _domain + "\\";

        /// <summary>quello che sta scritto nel campo utente (senza il chip del dominio)</summary>
        public string UserNameInput
        {
            get => _userNameInput;
            set { if (SetAndNotifyIfChanged(ref _userNameInput, value ?? "")) RaisePropertyChanged(nameof(UserName)); }
        }

        /// <summary>nome utente completo da usare per collegarsi</summary>
        public string UserName
        {
            get
            {
                var u = (_userNameInput ?? "").Trim();
                return HasDomain && u.Length > 0 && u.IndexOfAny(new[] { '\\', '@' }) < 0 ? _domain + "\\" + u : u;
            }
            set
            {
                value ??= "";
                // "DOMINIO\utente" con il dominio della cartella: il dominio va nel chip, il resto nel campo
                if (!string.IsNullOrEmpty(_domain) && value.StartsWith(_domain + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    _domainRemoved = false;
                    _userNameInput = value.Substring(_domain.Length + 1);
                }
                else
                {
                    _userNameInput = value;
                }
                RaisePropertyChanged(nameof(UserNameInput)); RaisePropertyChanged(nameof(HasDomain)); RaisePropertyChanged();
            }
        }

        private RelayCommand? _cmdRemoveDomain;
        public RelayCommand CmdRemoveDomain => _cmdRemoveDomain ??= new RelayCommand(_ =>
        {
            _domainRemoved = true;
            RaisePropertyChanged(nameof(HasDomain)); RaisePropertyChanged(nameof(UserName));
        });

        // password rifiutata dal server: il campo password diventa rosso con questo testo sotto
        private string _errorText = "";
        public string ErrorText
        {
            get => _errorText;
            set { if (SetAndNotifyIfChanged(ref _errorText, value ?? "")) RaisePropertyChanged(nameof(HasError)); }
        }
        public bool HasError => !string.IsNullOrEmpty(_errorText);

        // utenti delle credenziali condivise della cartella: un clic li usa e collega
        public class SuggestedUser
        {
            public string Display { get; init; } = "";
            public string UserName { get; init; } = "";
            public string Password { get; init; } = "";
            public string PrivateKey { get; init; } = "";
        }
        public System.Collections.Generic.List<SuggestedUser> SuggestedUsers { get; set; } = new System.Collections.Generic.List<SuggestedUser>();
        public bool HasSuggestions => SuggestedUsers.Count > 0;
        public string SuggestionsTitle { get; set; } = "";

        private RelayCommand? _cmdUseSuggested;
        public RelayCommand CmdUseSuggested => _cmdUseSuggested ??= new RelayCommand(o =>
        {
            if (o is not SuggestedUser u) return;
            UserName = u.UserName;
            if (!string.IsNullOrEmpty(u.PrivateKey)) { UsePrivateKeyForConnect = true; PrivateKey = u.PrivateKey; Password = ""; }
            else { UsePrivateKeyForConnect = false; Password = u.Password; }
            RequestClose(true);
        });

        /// <summary>
        /// false per i protocolli che usano solo la password (es. VNC): nasconde il campo utente e non lo richiede
        /// </summary>
        public bool IsUserNameVisible { get; set; } = true;

        private string _password = "";
        public string Password
        {
            get => _password;
            set => SetAndNotifyIfChanged(ref _password, value);
        }

        private bool _canUsePrivateKeyForConnect = true;
        public bool CanUsePrivateKeyForConnect
        {
            get => _canUsePrivateKeyForConnect;
            set => SetAndNotifyIfChanged(ref _canUsePrivateKeyForConnect, value);
        }

        private bool _usePrivateKeyForConnect = false;
        public bool UsePrivateKeyForConnect
        {
            get => _canUsePrivateKeyForConnect && _usePrivateKeyForConnect;
            set => SetAndNotifyIfChanged(ref _usePrivateKeyForConnect, CanUsePrivateKeyForConnect && value);
        }


        private bool _canRememberInfo = true;
        public bool CanRememberInfo
        {
            get => _canRememberInfo;
            set
            {
                if (SetAndNotifyIfChanged(ref _canRememberInfo, value))
                {
                    RaisePropertyChanged(nameof(IsRememberInfo));
                }
            }
        }

        public bool IsRememberInfo
        {
            get => _canRememberInfo && IoC.Get<LocalityService>().GetMisc<bool>($"{nameof(PasswordPopupDialogViewModel)}.{nameof(IsRememberInfo)}");
            set
            {
                IoC.Get<LocalityService>().SetMisc($"{nameof(PasswordPopupDialogViewModel)}.{nameof(IsRememberInfo)}", value.ToString());
                RaisePropertyChanged();
            }
        }

        private string _privateKey = "";
        public string PrivateKey
        {
            get => _privateKey;
            set => SetAndNotifyIfChanged(ref _privateKey, value);
        }

        public PasswordPopupDialogViewModel(bool canUsePrivateKeyForConnect = false, bool canRememberInfo = false)
        {
            CanUsePrivateKeyForConnect = canUsePrivateKeyForConnect;
            CanRememberInfo = canRememberInfo;
        }

        protected override void OnViewLoaded()
        {
            base.OnViewLoaded();
            if (View is PasswordPopupDialogView v)
            {
                v.Title = Title;

                v.TbUserName.Text = UserNameInput;
                v.TbPwd.Password = Password;

                if (!IsUserNameVisible || !string.IsNullOrEmpty(v.TbUserName.Text))
                {
                    v.TbPwd.Focus();
                    v.TbPwd.CaretIndex = v.TbPwd.Password.Length;
                }
                else
                {
                    v.TbUserName.Focus();
                    v.TbUserName.CaretIndex = v.TbUserName.Text.Length;
                }
            }
        }


        /// <summary>
        /// validate whether all fields are correct to save
        /// </summary>
        /// <returns></returns>
        public virtual bool CanSave()
        {
            return !IsUserNameVisible || !string.IsNullOrEmpty(UserName);
        }


        private RelayCommand? _cmdSave;
        public RelayCommand CmdSave
        {
            get
            {
                return _cmdSave ??= new RelayCommand((o) =>
                {
                    this.RequestClose(true);
                }, o => CanSave());
            }
        }


        private RelayCommand? _cmdQuit;
        public new RelayCommand CmdQuit
        {
            get
            {
                return _cmdQuit ??= new RelayCommand((o) =>
                {
                    this.RequestClose(false);
                });
            }
        }
    }
}
