using Lemora.Model.Protocol;
using Lemora.Model.Protocol.Base;
using Lemora.Utils;

namespace Lemora.Service
{
    /// <summary>
    /// TODO: make it utils
    /// </summary>
    public static class DataService
    {
        public static void EncryptToDatabaseLevel(this ProtocolBase server)
        {
            // encrypt password
            if (server is ProtocolBaseWithAddressPortUserPwd s)
            {
                // skip empty values: EncryptOnce("") yields a non-empty cipher, which
                // used to be assigned back and break save/load round trips (issue #1118)
                if (!string.IsNullOrEmpty(s.Password))
                    s.Password = DataVault.EncryptOnce(s.Password);
                foreach (var credential in s.AlternateCredentials)
                {
                    credential.EncryptToDatabaseLevel();
                }
                foreach (var user in s.Users)
                {
                    if (!string.IsNullOrEmpty(user.Password)) user.Password = DataVault.EncryptOnce(user.Password);
                    if (!string.IsNullOrEmpty(user.PrivateKey)) user.PrivateKey = DataVault.EncryptOnce(user.PrivateKey);
                }
            }
            switch (server)
            {
                case SSH ssh when !string.IsNullOrWhiteSpace(ssh.PrivateKey):
                    {
                        ssh.PrivateKey = DataVault.EncryptOnce(ssh.PrivateKey);
                        break;
                    }
                case RDP rdp when !string.IsNullOrWhiteSpace(rdp.GatewayPassword):
                    {
                        rdp.GatewayPassword = DataVault.EncryptOnce(rdp.GatewayPassword);
                        break;
                    }

                case LocalApp app:
                    foreach (var arg in app.ArgumentList)
                    {
                        if (arg.Type == AppArgumentType.Secret)
                        {
                            arg.Value = DataVault.EncryptOnce(arg.Value);
                        }
                    }
                    break;
            }
        }

        public static void DecryptToConnectLevel(this ProtocolBase server)
        {
            if (server is ProtocolBaseWithAddressPortUserPwd s)
            {
                if (!string.IsNullOrEmpty(s.Password))
                    s.Password = DataVault.DecryptOrReturnOriginalString(s.Password);
                foreach (var credential in s.AlternateCredentials)
                {
                    credential.DecryptToConnectLevel();
                }
                foreach (var user in s.Users)
                {
                    if (!string.IsNullOrEmpty(user.Password)) user.Password = DataVault.DecryptOrReturnOriginalString(user.Password);
                    if (!string.IsNullOrEmpty(user.PrivateKey)) user.PrivateKey = DataVault.DecryptOrReturnOriginalString(user.PrivateKey);
                }
            }
            switch (server)
            {
                case SSH ssh when !string.IsNullOrWhiteSpace(ssh.PrivateKey):
                    ssh.PrivateKey = DataVault.DecryptOrReturnOriginalString(ssh.PrivateKey);
                    break;

                case RDP rdp when !string.IsNullOrWhiteSpace(rdp.GatewayPassword):
                    rdp.GatewayPassword = DataVault.DecryptOrReturnOriginalString(rdp.GatewayPassword);
                    break;

                case LocalApp app:
                    foreach (var arg in app.ArgumentList)
                    {
                        if (arg.Type == AppArgumentType.Secret)
                        {
                            arg.Value = DataVault.DecryptOrReturnOriginalString(arg.Value);
                        }
                    }
                    break;
            }
        }


        public static void EncryptToDatabaseLevel(this Credential credential)
        {
            if (!string.IsNullOrEmpty(credential.Password))
                credential.Password = DataVault.EncryptOnce(credential.Password);
            if (!string.IsNullOrEmpty(credential.PrivateKeyPath))
                credential.PrivateKeyPath = DataVault.EncryptOnce(credential.PrivateKeyPath);
        }

        public static void DecryptToConnectLevel(this Credential credential)
        {
            if (!string.IsNullOrEmpty(credential.Password))
                credential.Password = DataVault.DecryptOrReturnOriginalString(credential.Password);
            if (!string.IsNullOrEmpty(credential.PrivateKeyPath))
                credential.PrivateKeyPath = DataVault.DecryptOrReturnOriginalString(credential.PrivateKeyPath);
        }
    }
}