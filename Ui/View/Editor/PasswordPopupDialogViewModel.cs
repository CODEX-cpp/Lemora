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

        // User = (folder domain, shown as a removable chip) + typed text. Without the chip, or with "domain\user" already typed,
        // the name stays as typed: typing only "administrator" uses the local user.
        private string _userNameInput = "";
        private string _domain = "";
        private bool _domainRemoved;

        /// <summary>domain suggested by the folder ("" = none)</summary>
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

        /// <summary>what is typed in the user field (without the domain chip)</summary>
        public string UserNameInput
        {
            get => _userNameInput;
            set { if (SetAndNotifyIfChanged(ref _userNameInput, value ?? "")) RaisePropertyChanged(nameof(UserName)); }
        }

        /// <summary>full user name to connect with</summary>
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
                // "DOMAIN\user" with the folder domain: the domain goes in the chip, the rest in the field
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

        // password rejected by the server: the password field turns red with this text below
        private string _errorText = "";
        public string ErrorText
        {
            get => _errorText;
            set { if (SetAndNotifyIfChanged(ref _errorText, value ?? "")) RaisePropertyChanged(nameof(HasError)); }
        }
        public bool HasError => !string.IsNullOrEmpty(_errorText);

        // users from the folder's shared credentials: one click uses them and connects
        public class SuggestedUser
        {
            public string Display { get; init; } = "";
            public string UserName { get; init; } = "";
            public string Password { get; init; } = "";
            public string PrivateKey { get; init; } = "";
            /// <summary>folder credential id: after a successful connection it becomes active on the server ("Use and connect")</summary>
            public string CredentialId { get; init; } = "";
        }
        /// <summary>folder credential chosen with "Use and connect" (empty if the user typed the credentials by hand)</summary>
        public string LinkCredentialId { get; private set; } = "";
        public System.Collections.Generic.List<SuggestedUser> SuggestedUsers { get; set; } = new System.Collections.Generic.List<SuggestedUser>();
        public bool HasSuggestions => SuggestedUsers.Count > 0;
        public string SuggestionsTitle { get; set; } = "";

        private RelayCommand? _cmdUseSuggested;
        public RelayCommand CmdUseSuggested => _cmdUseSuggested ??= new RelayCommand(o =>
        {
            if (o is not SuggestedUser u) return;
            LinkCredentialId = u.CredentialId;
            UserName = u.UserName;
            if (!string.IsNullOrEmpty(u.PrivateKey)) { UsePrivateKeyForConnect = true; PrivateKey = u.PrivateKey; Password = ""; }
            else { UsePrivateKeyForConnect = false; Password = u.Password; }
            RequestClose(true);
        });

        /// <summary>
        /// false for protocols that only use the password (e.g. VNC): hides the user field and does not require it
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

        // "Save to connection": for already saved connections, the credentials typed here become a user of the connection (after a successful connection)
        public bool CanSaveToConnection { get; set; }
        public string SaveToConnectionText => Lemora.Setup.SetupText.T("pw_save_conn");
        private const string SaveToConnectionKey = "PasswordPopupDialogViewModel.SaveToConnection";
        /// <summary>on by default (credentials are no longer lost at every connection); the last choice is remembered</summary>
        public bool SaveToConnection
        {
            get
            {
                try { var v = IoC.Get<LocalityService>().GetMisc<string>(SaveToConnectionKey); return v != "0"; }
                catch { return true; }
            }
            set
            {
                try { IoC.Get<LocalityService>().SetMisc(SaveToConnectionKey, value ? "1" : "0"); } catch { /* not remembered */ }
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
