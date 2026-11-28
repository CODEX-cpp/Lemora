using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
        /// <summary>local users of this server (in addition to the folder's shared credentials)</summary>
        public ObservableCollection<UserEntry> Users
        {
            get => _users ??= new ObservableCollection<UserEntry>();
            set => SetAndNotifyIfChanged(ref _users, value);
        }

        private List<string>? _activeFolderCredentialIds = new List<string>();
        /// <summary>ids of the folder's shared credentials enabled on this server</summary>
        public List<string> ActiveFolderCredentialIds
        {
            get => _activeFolderCredentialIds ??= new List<string>();
            set => SetAndNotifyIfChanged(ref _activeFolderCredentialIds, value);
        }

        private string _defaultUserKey = "";
        /// <summary>user used at login without asking ("L:name" or "F:id"); empty = the first one</summary>
        public string DefaultUserKey
        {
            get => _defaultUserKey;
            set => SetAndNotifyIfChanged(ref _defaultUserKey, value ?? "");
        }

        private bool? _askWhichUser = false;
        /// <summary>if true, the user is chosen when connecting</summary>
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

        // random key, new at every start, kept only in memory: makes the credential fingerprint in the session id
        // impossible to trace back to the password. The id ends up in the log and in the replies to Claude; it used to contain an MD5 of the
        // plaintext password and the user, which a process of the same user could use to guess the password offline (security review).
        private static readonly byte[] s_sessionIdKey = RandomNumberGenerator.GetBytes(32);

        /// <summary>
        /// build the id for host
        /// </summary>
        /// <returns></returns>
        public override string BuildConnectionId()
        {
            // no user and nothing derived from the plaintext password: only an HMAC with the random key above, which still tells
            // different credentials apart (two tabs to the same PC with different users) without any way back to the password.
            using var h = new HMACSHA256(s_sessionIdKey);
            var tag = Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(UserName + "\n" + Password)))[..16].ToLowerInvariant();
            return $"{Id}_{Address}:{Port}(#{tag})";
        }
    }
}
