using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Shawn.Utils;

namespace Lemora.Model.Protocol.Base
{
    public abstract class ProtocolBaseWithAddressPortUserPwd : ProtocolBaseWithAddressPort
    {
        protected ProtocolBaseWithAddressPortUserPwd(string protocol, string classVersion, string protocolDisplayName) : base(protocol, classVersion, protocolDisplayName)
        {
        }

        #region Conn


        private string _inheritedCredentialName = "";
        public string InheritedCredentialName
        {
            get => _inheritedCredentialName;
            set => SetAndNotifyIfChanged(ref _inheritedCredentialName, value);
        }

        public const string MACRO_USERNAME = "%LM_USERNAME%";
        private string _userName = "";
        [OtherName(Name = "LM_USERNAME")]
        public string UserName
        {
            get => _userName;
            set
            {
                if (SetAndNotifyIfChanged(ref _userName, value))
                    RaisePropertyChanged(nameof(SubTitle));
            }
        }



        private ObservableCollection<UserEntry>? _users = new ObservableCollection<UserEntry>();
        /// <summary>utenti locali di questo server (oltre alle credenziali condivise della cartella)</summary>
        public ObservableCollection<UserEntry> Users
        {
            get => _users ??= new ObservableCollection<UserEntry>();
            set => SetAndNotifyIfChanged(ref _users, value);
        }

        private List<string>? _activeFolderCredentialIds = new List<string>();
        /// <summary>id delle credenziali condivise della cartella attive su questo server</summary>
        public List<string> ActiveFolderCredentialIds
        {
            get => _activeFolderCredentialIds ??= new List<string>();
            set => SetAndNotifyIfChanged(ref _activeFolderCredentialIds, value);
        }

        private string _defaultUserKey = "";
        /// <summary>utente usato al login senza chiedere ("L:nome" o "F:id"); vuoto = il primo</summary>
        public string DefaultUserKey
        {
            get => _defaultUserKey;
            set => SetAndNotifyIfChanged(ref _defaultUserKey, value ?? "");
        }

        private bool? _askWhichUser = false;
        /// <summary>se vero, alla connessione si sceglie l'utente</summary>
        [DefaultValue(null)]
        public bool? AskWhichUser
        {
            get => _askWhichUser;
            set => SetAndNotifyIfChanged(ref _askWhichUser, value);
        }

        private bool? _askPasswordWhenConnect = false;
        [DefaultValue(null)]
        public bool? AskPasswordWhenConnect
        {
            get => _askPasswordWhenConnect;
            set => SetAndNotifyIfChanged(ref _askPasswordWhenConnect, value);
        }

        public const string MACRO_PASSWORD = "%LM_PASSWORD%";
        private string _password = "";
        [OtherName(Name = "LM_PASSWORD")]
        // NOTE: keep this setter side-effect free (issue #1118): data-layer code
        // (EncryptToDatabaseLevel / json deserialization) assigns this property too,
        // and any "clear the private key here" logic wiped saved key paths.
        // The password/key mutual exclusion lives in CredentialViewModel.
        public string Password
        {
            get => _password;
            set => SetAndNotifyIfChanged(ref _password, value);
        }

        private bool? _usePrivateKeyForConnect;
        [DefaultValue(null)]
        public bool? UsePrivateKeyForConnect
        {
            get => _usePrivateKeyForConnect;
            set => SetAndNotifyIfChanged(ref _usePrivateKeyForConnect, value);
        }

        public const string MACRO_PRIVATE_KEY_PATH = "%LM_PRIVATE_KEY_PATH%";
        private string _privateKey = "";
        [OtherName(Name = "LM_PRIVATE_KEY_PATH")]
        // NOTE: keep this setter side-effect free (issue #1118), see Password above.
        public string PrivateKey
        {
            get => _privateKey;
            set => SetAndNotifyIfChanged(ref _privateKey, value);
        }

        /// <summary>
        /// return true if private key is all ascii
        /// </summary>
        public bool IsPrivateKeyAllAscii()
        {
            return PrivateKey.All(c => c < 128);
        }

        protected override string GetSubTitle()
        {
            return string.IsNullOrEmpty(UserName) ? base.GetSubTitle() : $"{Address}:{Port} ({UserName})";
        }



        public override Credential GetCredential()
        {
            var c = new Credential()
            {
                Name = InheritedCredentialName,
                Address = Address,
                Port = Port,
                Password = Password,
                UserName = UserName,
                PrivateKeyPath = PrivateKey,
            };
            return c;
        }

        public override void SetCredential(in Credential credential, bool ignoreEmptyString)
        {
            base.SetCredential(credential, ignoreEmptyString);

            if (!ignoreEmptyString || !string.IsNullOrEmpty(credential.UserName))
            {
                UserName = credential.UserName;
            }


            if (!ignoreEmptyString || !string.IsNullOrEmpty(credential.PrivateKeyPath))
            {
                PrivateKey = credential.PrivateKeyPath;
                Password = "";
            }
            else if (!ignoreEmptyString || !string.IsNullOrEmpty(credential.Password))
            {
                Password = credential.Password;
                PrivateKey = "";
            }
            UsePrivateKeyForConnect = !string.IsNullOrEmpty(PrivateKey);
        }

        #endregion

        /// <summary>
        /// return true if show username input
        /// </summary>
        public virtual bool ShowUserNameInput()
        {
            return true;
        }

        /// <summary>
        /// return true if show password input
        /// </summary>
        public virtual bool ShowPasswordInput()
        {
            return true;
        }

        /// <summary>
        /// return true if show private key input
        /// </summary>
        public virtual bool ShowPrivateKeyInput()
        {
            return false;
        }

        /// <summary>
        /// build the id for host
        /// </summary>
        /// <returns></returns>
        public override string BuildConnectionId()
        {
            return $"{Id}_{Address}:{Port}({MD5Helper.GetMd5Hash16BitString(Password)}@{UserName})";
        }
    }
}
