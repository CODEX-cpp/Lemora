using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace Lemora.Service
{
    /// <summary>una connessione dentro un file .mora: dove collegarsi e come aprirla, mai la password</summary>
    public class MoraConnection
    {
        public string Protocol { get; set; } = "";
        public string Name { get; set; } = "";
        public string Address { get; set; } = "";
        public string Port { get; set; } = "";
        public string UserName { get; set; } = "";
        public string Domain { get; set; } = "";
        /// <summary>apri in una finestra separata invece che in una scheda della finestra principale</summary>
        public bool NewWindow { get; set; }
    }

    /// <summary>
    /// Formato del file .mora e sua registrazione in Windows.
    /// Il file non e' leggibile (compresso e cifrato) e non contiene password: la chiave e' dentro l'app, quindi e' un'illeggibilita' per chi apre il file
    /// con un editor, non una protezione di segreti. Per questo non ci vanno segreti.
    /// </summary>
    public static class MoraFile
    {
        public const string Extension = ".mora";
        private const string ProgId = "Lemora.Connection";
        private static readonly byte[] Magic = { (byte)'M', (byte)'O', (byte)'R', (byte)'A', 1 };   // 4 lettere e versione del formato
        private static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("Lemora.mora.v1/4f1c9a7e-connection-file"));

        public static void Write(string path, MoraConnection c)
        {
            var json = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(c));
            using var packed = new MemoryStream();
            using (var gz = new GZipStream(packed, CompressionLevel.SmallestSize, true)) gz.Write(json);
            var plain = packed.ToArray();

            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var cipher = new byte[plain.Length];
            using (var aes = new AesGcm(Key, 16)) aes.Encrypt(nonce, plain, cipher, tag, Magic);

            using var f = File.Create(path);
            f.Write(Magic); f.Write(nonce); f.Write(tag); f.Write(cipher);
        }

        /// <summary>null se il file non e' un .mora valido (o e' di una versione piu' nuova)</summary>
        public static MoraConnection? Read(string path)
        {
            try
            {
                var all = File.ReadAllBytes(path);
                if (all.Length < Magic.Length + 12 + 16 + 1) return null;
                for (var i = 0; i < Magic.Length; i++) if (all[i] != Magic[i]) return null;
                var nonce = all.AsSpan(Magic.Length, 12);
                var tag = all.AsSpan(Magic.Length + 12, 16);
                var cipher = all.AsSpan(Magic.Length + 28);
                var plain = new byte[cipher.Length];
                using (var aes = new AesGcm(Key, 16)) aes.Decrypt(nonce, cipher, tag, plain, Magic);
                using var gz = new GZipStream(new MemoryStream(plain), CompressionMode.Decompress);
                using var sr = new StreamReader(gz, Encoding.UTF8);
                var c = JsonConvert.DeserializeObject<MoraConnection>(sr.ReadToEnd());
                return c == null || string.IsNullOrWhiteSpace(c.Protocol) || string.IsNullOrWhiteSpace(c.Address) ? null : c;
            }
            catch
            {
                return null;   // file rovinato, modificato a mano o non nostro
            }
        }

        public static string SafeFileName(string name)
        {
            foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
            name = name.Trim().TrimEnd('.');
            return name.Length == 0 ? "Lemora" : name;
        }

        /// <summary>
        /// Dice a Windows che i file .mora si aprono con questa copia di Lemora (e con la sua icona).
        /// machine = per tutti gli utenti (serve l'amministratore: installazione e aggiornamento); altrimenti solo per l'utente corrente (pulsante in Impostazioni).
        /// </summary>
        public static bool Register(string exePath, bool machine)
        {
            try
            {
                var root = machine ? Registry.LocalMachine : Registry.CurrentUser;
                using (var k = root.CreateSubKey(@"Software\Classes\" + Extension)) k.SetValue("", ProgId);
                using (var k = root.CreateSubKey(@"Software\Classes\" + ProgId))
                {
                    k.SetValue("", "Lemora");
                    using var icon = k.CreateSubKey("DefaultIcon"); icon.SetValue("", $"\"{exePath}\",0");
                    using var cmd = k.CreateSubKey(@"shell\open\command"); cmd.SetValue("", $"\"{exePath}\" \"%1\"");
                }
                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);   // SHCNE_ASSOCCHANGED: Esplora risorse rilegge le icone
                return true;
            }
            catch (Exception e)
            {
                Debug.WriteLine("MoraFile.Register: " + e.Message);
                return false;
            }
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    }
}
