using System;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Lemora.Service.Locality;
using Shawn.Utils;

namespace Lemora.Utils
{
    /// <summary>
    /// Strumento di sviluppo (solo con LEMORA_DEMO=vnc-probe): si collega a un server VNC e REGISTRA NEL LOG cosa dichiara
    /// (nome, dimensione dello schermo e, se il server li manda, i monitor con posizione e misure: estensione ExtendedDesktopSize).
    /// Sola lettura: nessun movimento di mouse, nessun tasto, nessuna scrittura sul PC remoto. La password viene letta dalla cronologia
    /// delle connessioni rapide e non viene mai scritta nel log.
    /// </summary>
    public static class VncProbe
    {
        private static void Log(string s) => SimpleLogHelper.Warning("PROBE " + s);

        public static async Task RunAsync(string host, int port)
        {
            try
            {
                var saved = LocalityConnectRecorder.QuickConnectionHistoryGetAll().FirstOrDefault(x => x.Host.StartsWith(host, StringComparison.OrdinalIgnoreCase) && x.Protocol == "VNC");
                var password = saved?.GetUserPassword().password ?? "";
                Log($"{host}:{port} password salvata: {(string.IsNullOrEmpty(password) ? "NO" : "si")}");

                using var tcp = new TcpClient();
                await tcp.ConnectAsync(host, port);
                using var ns = tcp.GetStream();
                ns.ReadTimeout = 8000;

                async Task<byte[]> Read(int n)
                {
                    var buf = new byte[n]; var off = 0;
                    while (off < n) { var r = await ns.ReadAsync(buf, off, n - off); if (r <= 0) throw new Exception("connessione chiusa"); off += r; }
                    return buf;
                }
                ushort U16(byte[] b, int o) => unchecked((ushort)((b[o] << 8) | b[o + 1]));
                uint U32(byte[] b, int o) => unchecked((uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]));
                async Task Send(params byte[] b) => await ns.WriteAsync(b, 0, b.Length);

                var ver = Encoding.ASCII.GetString(await Read(12)).Trim();
                Log("versione server: " + ver);
                await Send(Encoding.ASCII.GetBytes("RFB 003.008\n"));

                var nTypes = (await Read(1))[0];
                if (nTypes == 0) { Log("il server rifiuta: " + Encoding.UTF8.GetString(await Read((int)U32(await Read(4), 0)))); return; }
                var types = await Read(nTypes);
                Log("sicurezza offerta: " + string.Join(",", types));
                if (types.Contains((byte)2))
                {
                    await Send(2);
                    var challenge = await Read(16);
                    // chiave DES di VNC: i bit di ogni byte della password (max 8) sono invertiti
                    var key = new byte[8];
                    var pw = Encoding.Latin1.GetBytes(password);
                    for (var i = 0; i < 8 && i < pw.Length; i++)
                    {
                        var b = pw[i]; byte r = 0;
                        for (var k = 0; k < 8; k++) r |= (byte)(((b >> k) & 1) << (7 - k));
                        key[i] = r;
                    }
                    using var des = DES.Create(); des.Mode = CipherMode.ECB; des.Padding = PaddingMode.None; des.Key = key;
                    using var enc = des.CreateEncryptor();
                    await Send(enc.TransformFinalBlock(challenge, 0, 16));
                }
                else if (types.Contains((byte)1)) await Send(1);
                else { Log("nessun tipo di sicurezza gestito"); return; }

                var result = U32(await Read(4), 0);
                Log("esito autenticazione: " + (result == 0 ? "ok" : "RIFIUTATA"));
                if (result != 0) return;

                await Send(1);   // ClientInit: connessione condivisa (non scollega gli altri visualizzatori)
                var init = await Read(24);
                var w = U16(init, 0); var h = U16(init, 2);
                var name = Encoding.UTF8.GetString(await Read((int)U32(init, 20)));
                Log($"schermo dichiarato: {w}x{h}, bit per pixel {init[4]}, nome '{name}'");

                // codifiche richieste: Raw, CopyRect, e le due che portano le informazioni sui monitor (DesktopSize, ExtendedDesktopSize)
                var encs = new int[] { 0, 1, -223, -308, -224 };
                var msg = new byte[4 + encs.Length * 4];
                msg[0] = 2; msg[2] = (byte)(encs.Length >> 8); msg[3] = (byte)encs.Length;
                unchecked { for (var i = 0; i < encs.Length; i++) { var v = encs[i]; msg[4 + i * 4] = (byte)(v >> 24); msg[5 + i * 4] = (byte)(v >> 16); msg[6 + i * 4] = (byte)(v >> 8); msg[7 + i * 4] = (byte)v; } }
                await Send(msg);
                // richiesta di aggiornamento di un solo pixel: serve solo a far rispondere il server (nessuna azione sul PC remoto)
                await Send(3, 0, 0, 0, 0, 0, 0, 1, 0, 1);

                var bpp = Math.Max(1, init[4] / 8);
                var until = DateTime.Now.AddSeconds(5);
                var gotUpdate = false;
                while (DateTime.Now < until && !gotUpdate)
                {
                    var t = (await Read(1))[0];
                    if (t == 0)
                    {
                        var hdr = await Read(3); var n = U16(hdr, 1);
                        Log($"aggiornamento con {n} rettangoli");
                        for (var i = 0; i < n; i++)
                        {
                            var rh = await Read(12);
                            var x = U16(rh, 0); var y = U16(rh, 2); var rw = U16(rh, 4); var rhh = U16(rh, 6); var e = unchecked((int)U32(rh, 8));
                            Log($"  rettangolo {i}: codifica {e} pos {x},{y} misure {rw}x{rhh}");
                            if (e == 0) await Read(rw * rhh * bpp);
                            else if (e == 1) await Read(4);
                            else if (e == -308)
                            {
                                var sh = await Read(4); var ns2 = sh[0];
                                Log($"  >> MONITOR DICHIARATI DAL SERVER: {ns2}");
                                for (var s = 0; s < ns2; s++)
                                {
                                    var sc = await Read(16);
                                    Log($"     monitor id {U32(sc, 0)}: posizione {U16(sc, 4)},{U16(sc, 6)} misure {U16(sc, 8)}x{U16(sc, 10)}");
                                }
                            }
                        }
                        gotUpdate = true;
                    }
                    else if (t == 2) { Log("(campanello)"); }
                    else { Log("messaggio del server di tipo " + t + ": mi fermo"); break; }
                }
                Log("fine della prova (nessuna azione eseguita sul PC remoto)");
            }
            catch (Exception e)
            {
                Log("errore: " + e.Message);
            }
        }
    }
}
