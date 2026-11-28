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
    /// Development tool (only with LEMORA_DEMO=vnc-probe): connects to a VNC server and WRITES TO THE LOG what it declares
    /// (name, screen size and, if the server sends them, the monitors with position and size: ExtendedDesktopSize extension).
    /// Read only: no mouse movement, no keys, no writes on the remote PC. The password is read from the quick connect
    /// history and is never written to the log.
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
                Log($"{host}:{port} saved password: {(string.IsNullOrEmpty(password) ? "NO" : "yes")}");

                using var tcp = new TcpClient();
                await tcp.ConnectAsync(host, port);
                using var ns = tcp.GetStream();
                ns.ReadTimeout = 8000;

                async Task<byte[]> Read(int n)
                {
                    var buf = new byte[n]; var off = 0;
                    while (off < n) { var r = await ns.ReadAsync(buf, off, n - off); if (r <= 0) throw new Exception("connection closed"); off += r; }
                    return buf;
                }
                ushort U16(byte[] b, int o) => unchecked((ushort)((b[o] << 8) | b[o + 1]));
                uint U32(byte[] b, int o) => unchecked((uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]));
                async Task Send(params byte[] b) => await ns.WriteAsync(b, 0, b.Length);

                var ver = Encoding.ASCII.GetString(await Read(12)).Trim();
                Log("server version: " + ver);
                await Send(Encoding.ASCII.GetBytes("RFB 003.008\n"));

                var nTypes = (await Read(1))[0];
                if (nTypes == 0) { Log("the server refuses: " + Encoding.UTF8.GetString(await Read((int)U32(await Read(4), 0)))); return; }
                var types = await Read(nTypes);
                Log("security offered: " + string.Join(",", types));
                if (types.Contains((byte)2))
                {
                    await Send(2);
                    var challenge = await Read(16);
                    // VNC DES key: the bits of each password byte (max 8) are reversed
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
                else { Log("no supported security type"); return; }

                var result = U32(await Read(4), 0);
                Log("authentication result: " + (result == 0 ? "ok" : "REJECTED"));
                if (result != 0) return;

                await Send(1);   // ClientInit: shared connection (does not disconnect the other viewers)
                var init = await Read(24);
                var w = U16(init, 0); var h = U16(init, 2);
                var name = Encoding.UTF8.GetString(await Read((int)U32(init, 20)));
                Log($"declared screen: {w}x{h}, bits per pixel {init[4]}, name '{name}'");

                // requested encodings: Raw, CopyRect, and the two that carry the monitor information (DesktopSize, ExtendedDesktopSize)
                var encs = new int[] { 0, 1, -223, -308, -224 };
                var msg = new byte[4 + encs.Length * 4];
                msg[0] = 2; msg[2] = (byte)(encs.Length >> 8); msg[3] = (byte)encs.Length;
                unchecked { for (var i = 0; i < encs.Length; i++) { var v = encs[i]; msg[4 + i * 4] = (byte)(v >> 24); msg[5 + i * 4] = (byte)(v >> 16); msg[6 + i * 4] = (byte)(v >> 8); msg[7 + i * 4] = (byte)v; } }
                await Send(msg);
                // update request for a single pixel: only makes the server answer (no action on the remote PC)
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
                        Log($"update with {n} rectangles");
                        for (var i = 0; i < n; i++)
                        {
                            var rh = await Read(12);
                            var x = U16(rh, 0); var y = U16(rh, 2); var rw = U16(rh, 4); var rhh = U16(rh, 6); var e = unchecked((int)U32(rh, 8));
                            Log($"  rectangle {i}: encoding {e} pos {x},{y} size {rw}x{rhh}");
                            if (e == 0) await Read(rw * rhh * bpp);
                            else if (e == 1) await Read(4);
                            else if (e == -308)
                            {
                                var sh = await Read(4); var ns2 = sh[0];
                                Log($"  >> MONITORS DECLARED BY THE SERVER: {ns2}");
                                for (var s = 0; s < ns2; s++)
                                {
                                    var sc = await Read(16);
                                    Log($"     monitor id {U32(sc, 0)}: position {U16(sc, 4)},{U16(sc, 6)} size {U16(sc, 8)}x{U16(sc, 10)}");
                                }
                            }
                        }
                        gotUpdate = true;
                    }
                    else if (t == 2) { Log("(bell)"); }
                    else { Log("server message of type " + t + ": stopping"); break; }
                }
                Log("end of the probe (no action performed on the remote PC)");
            }
            catch (Exception e)
            {
                Log("error: " + e.Message);
            }
        }
    }
}
