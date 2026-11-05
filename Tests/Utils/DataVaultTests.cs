using System.Linq;
using Lemora.Utils;
using Xunit;

namespace Tests.Utils
{
    public class DataVaultTests
    {
        private static readonly byte[] Key = Enumerable.Repeat((byte)7, 32).ToArray();

        [Fact]
        public void EncryptDecrypt_RoundTrip_AndDifferentEachTime()
        {
            DataVault.SetKey(Key);
            var a = DataVault.SimpleEncrypt("segreto");
            var b = DataVault.SimpleEncrypt("segreto");
            Assert.StartsWith("lv1:", a);
            Assert.NotEqual(a, b);   // nonce nuovo ogni volta
            Assert.Equal("segreto", DataVault.SimpleDecrypt(a));
            Assert.Equal("segreto", DataVault.DecryptOrReturnOriginalString(b));
        }

        [Fact]
        public void WrongKey_OrTampered_ReturnsNull()
        {
            DataVault.SetKey(Key);
            var c = DataVault.SimpleEncrypt("segreto");
            var tampered = c.Substring(0, c.Length - 4) + (c.EndsWith("AAAA") ? "BBBB" : "AAAA");
            Assert.Null(DataVault.SimpleDecrypt(tampered));
            // chiave sbagliata, senza cambiare la chiave globale (gli altri test girano in parallelo e la usano)
            var locked = DataVault.Lock(Key, Enumerable.Repeat((byte)1, 32).ToArray());
            Assert.Null(DataVault.Unlock(locked, Enumerable.Repeat((byte)2, 32).ToArray()));
            Assert.Equal("testo in chiaro", DataVault.DecryptOrReturnOriginalString("testo in chiaro"));
        }

        [Fact]
        public void EncryptOnce_DoesNotDoubleEncrypt()
        {
            DataVault.SetKey(Key);
            var c = DataVault.EncryptOnce("pw");
            Assert.Equal(c, DataVault.EncryptOnce(c));
        }

        [Fact]
        public void Header_PasswordUnlocks_ChangeKeepsSameDataKey()
        {
            var dataKey = DataVault.NewKey();
            var h = DataVault.NewHeader(dataKey, "password-uno");
            Assert.Null(DataVault.UnlockWithPassword(h, "sbagliata"));
            Assert.Equal(dataKey, DataVault.UnlockWithPassword(h, "password-uno"));
            var h2 = DataVault.ParseHeader(DataVault.HeaderToJson(h))!;
            DataVault.SetPassword(h2, dataKey, "password-due");
            Assert.Null(DataVault.UnlockWithPassword(h2, "password-uno"));
            Assert.Equal(dataKey, DataVault.UnlockWithPassword(h2, "password-due"));
        }
    }
}
