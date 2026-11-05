using System;
using Shawn.Utils;

namespace Lemora.Model.Protocol.Base
{
    /// <summary>utente locale di un server (nome, password oppure chiave privata). Le password sono cifrate nel database.</summary>
    public class UserEntry : NotifyPropertyChangedBase, ICloneable
    {
        private string _userName = "";
        public string UserName
        {
            get => _userName;
            set => SetAndNotifyIfChanged(ref _userName, value);
        }

        private string _password = "";
        public string Password
        {
            get => _password;
            set => SetAndNotifyIfChanged(ref _password, value);
        }

        private string _privateKey = "";
        /// <summary>percorso del file chiave (.ppk), per SSH e SFTP</summary>
        public string PrivateKey
        {
            get => _privateKey;
            set => SetAndNotifyIfChanged(ref _privateKey, value);
        }

        private bool _usePrivateKey;
        public bool UsePrivateKey
        {
            get => _usePrivateKey;
            set => SetAndNotifyIfChanged(ref _usePrivateKey, value);
        }

        public object Clone() => MemberwiseClone();
        public UserEntry CloneMe() => (UserEntry)MemberwiseClone();
    }
}
