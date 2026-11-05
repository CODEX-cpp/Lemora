using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lemora.Service.FileTransfer;

// Prova del backend di trasferimento file sul PC laboratorio. Lavora solo in /C:/provalemora e in una cartella locale temporanea.
Console.OutputEncoding = Encoding.UTF8;
var host = args[0]; var port = int.Parse(args[1]);
var only = args.Length > 2 ? args[2] : "";
var pwd = Environment.GetEnvironmentVariable("LABVNC") ?? throw new Exception("manca LABVNC");
FileTransferManager.DiagLog = m => Console.WriteLine("   [log] " + m);
var pass = 0; var fail = 0;
void Check(string name, bool ok, string extra = "") { if (ok) pass++; else fail++; Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(extra.Length > 0 ? "  -> " + extra : "")}"); }

var work = Path.Combine(AppContext.BaseDirectory, "ftwork");
if (Directory.Exists(work)) Directory.Delete(work, true);
Directory.CreateDirectory(work);
var src = Path.Combine(work, "ft_src");
var rnd = new Random(7);
void WriteRandom(string path, int bytes) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); var b = new byte[bytes]; rnd.NextBytes(b); File.WriteAllBytes(path, b); }
WriteRandom(Path.Combine(src, "a.txt"), 1234);
File.WriteAllBytes(Path.Combine(src, "vuoto.txt"), Array.Empty<byte>());
WriteRandom(Path.Combine(src, "Cartella è€", "perché.txt"), 777);
WriteRandom(Path.Combine(src, "Cartella è€", "sub", "日本語.bin"), 3 * 1024 * 1024);
WriteRandom(Path.Combine(src, "grande.bin"), 60 * 1024 * 1024);
Directory.CreateDirectory(Path.Combine(src, "vuota"));
File.SetLastWriteTimeUtc(Path.Combine(src, "a.txt"), new DateTime(2024, 3, 5, 10, 20, 30, DateTimeKind.Utc));

const string D = "/C:/provalemora";
var mgr = new FileTransferManager(ct => TightFtConnection.ConnectAsync(host, port, pwd, ct));
mgr.Options.DownloadPipelineDepth = 4;

async Task Mk(string p) { try { await mgr.MkdirAsync(p); } catch (FileTransferException e) when (e.Kind == FtErrorKind.Exists) { } }
string Sha(string f) { using var s = File.OpenRead(f); return Convert.ToHexString(SHA256.HashData(s))[..12]; }
Dictionary<string, string> LocalTree(string root) => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
    .ToDictionary(p => Path.GetRelativePath(root, p).Replace('\\', '/'), p => Directory.Exists(p) ? "<dir>" : Sha(p), StringComparer.OrdinalIgnoreCase);
async Task<Dictionary<string, long>> RemoteTree(string wire, string rel = "")
{
    var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    foreach (var e in await mgr.ListAsync(wire))
    {
        var r = rel.Length == 0 ? e.Name : rel + "/" + e.Name;
        if (e.IsDirectory) { d[r] = -1; foreach (var kv in await RemoteTree(e.FullPath, r)) d[kv.Key] = kv.Value; } else d[r] = e.Size;
    }
    return d;
}
async Task<TransferJob> Run(TransferJob j) { await j.Completion; Console.WriteLine($"   job '{j.Title}' -> {j.State}, {j.DoneFiles}/{j.TotalFiles} file, {FileTransferManager.Fmt(j.DoneBytes)}/{FileTransferManager.Fmt(j.TotalBytes)}" + (j.Error != null ? $", errore: {j.Error.Message}" : "")); return j; }

try
{
    Console.WriteLine("== T0 collegamento");
    Check("T0 trasferimento file disponibile", await mgr.CheckAvailableAsync());
    var drives = await mgr.ListAsync("/");
    Check("T0 elenco dischi", drives.Any(d => d.Name == "C:"), string.Join(",", drives.Select(d => d.Name)));
    try { await mgr.DeleteAsync(new RemoteEntry("/C:", "provalemora", true, 0, DateTime.MinValue)); } catch (FileTransferException) { }   // eventuali resti

    if (only is "" or "1")
    {
        Console.WriteLine("== T1 carico albero (accenti, vuoto, 60 MB)");
        await mgr.MkdirAsync(D);
        var sw = Stopwatch.StartNew();
        var up = await Run(mgr.EnqueueUpload(new[] { src }, D));
        Check("T1 stato Completed", up.State == TransferState.Completed, up.State.ToString());
        Console.WriteLine($"   carico in {sw.Elapsed.TotalSeconds:F1} s ({up.TotalBytes / 1048576.0 / sw.Elapsed.TotalSeconds:F1} MB/s)");
        var rt = await RemoteTree(D + "/ft_src");
        var lt = LocalTree(src);
        var missing = lt.Keys.Where(k => !rt.ContainsKey(k)).ToList();
        Check("T1 stessi nomi sul remoto", missing.Count == 0 && rt.Count == lt.Count, "mancano: " + string.Join(";", missing));
        Check("T1 dimensioni uguali", lt.All(kv => kv.Value == "<dir>" || (rt.TryGetValue(kv.Key, out var s) && s == new FileInfo(Path.Combine(src, kv.Key.Replace('/', '\\'))).Length)));
        Check("T1 nessun file provvisorio", !rt.Keys.Any(k => k.EndsWith(FileTransferManager.PartSuffix)));

        Console.WriteLine("== T2 scarico albero");
        var dst = Path.Combine(work, "ft_dst");
        var root = (await mgr.ListAsync(D)).First(e => e.Name == "ft_src");
        sw.Restart();
        var dn = await Run(mgr.EnqueueDownload(new[] { root }, dst));
        Check("T2 stato Completed", dn.State == TransferState.Completed, dn.State.ToString());
        Console.WriteLine($"   scarico in {sw.Elapsed.TotalSeconds:F1} s ({dn.TotalBytes / 1048576.0 / sw.Elapsed.TotalSeconds:F1} MB/s)");
        var dl = LocalTree(Path.Combine(dst, "ft_src"));
        Check("T2 contenuto identico (SHA256 di ogni file)", dl.Count == lt.Count && lt.All(kv => dl.TryGetValue(kv.Key, out var h) && h == kv.Value), $"{dl.Count} voci");
        var mt = File.GetLastWriteTimeUtc(Path.Combine(dst, "ft_src", "a.txt"));
        Check("T2 data di modifica mantenuta", Math.Abs((mt - new DateTime(2024, 3, 5, 10, 20, 30, DateTimeKind.Utc)).TotalSeconds) < 3, mt.ToString("o"));
        Check("T2 percorsi di risultato", dn.ResultPaths.Count == 1 && Directory.Exists(dn.ResultPaths[0]), string.Join(";", dn.ResultPaths));
    }

    if (only is "" or "2")
    {
        Console.WriteLine("== T3 conflitti");
        var one = Path.Combine(work, "ft_src", "a.txt");
        mgr.Options.Conflict = ConflictPolicy.Skip;
        var j = await Run(mgr.EnqueueUpload(new[] { one }, D + "/ft_src"));
        Check("T3 Skip: file saltato", j.State == TransferState.CompletedWithSkips && j.Items.Any(i => i.Status.StartsWith("saltato")), j.State.ToString());
        mgr.Options.Conflict = ConflictPolicy.KeepBoth;
        j = await Run(mgr.EnqueueUpload(new[] { one }, D + "/ft_src"));
        var names = (await mgr.ListAsync(D + "/ft_src")).Select(e => e.Name).ToList();
        Check("T3 KeepBoth: nasce 'a (2).txt'", names.Contains("a (2).txt"), string.Join(",", names));
        mgr.Options.Conflict = ConflictPolicy.Ask;
        var asked = 0;
        mgr.ConflictResolver = ci => { asked++; return Task.FromResult(new ConflictDecision(ConflictAction.Overwrite, true)); };
        j = await Run(mgr.EnqueueUpload(new[] { one, Path.Combine(work, "ft_src", "vuoto.txt") }, D + "/ft_src"));
        Check("T3 Ask: chiesto una volta, vale per tutti", asked == 1 && j.State == TransferState.Completed, $"chieste {asked} volte");
        mgr.ConflictResolver = null; mgr.Options.Conflict = ConflictPolicy.Overwrite;
        j = await Run(mgr.EnqueueUpload(new[] { one }, D + "/ft_src"));
        Check("T3 Overwrite", j.State == TransferState.Completed);
        // scarico con conflitto locale
        var local = Path.Combine(work, "conf"); Directory.CreateDirectory(local); File.WriteAllText(Path.Combine(local, "a.txt"), "locale");
        mgr.Options.Conflict = ConflictPolicy.KeepBoth;
        var ent = (await mgr.ListAsync(D + "/ft_src")).First(e => e.Name == "a.txt");
        j = await Run(mgr.EnqueueDownload(new[] { ent }, local));
        Check("T3 scarico KeepBoth: 'a (2).txt' locale", File.Exists(Path.Combine(local, "a (2).txt")) && File.ReadAllText(Path.Combine(local, "a.txt")) == "locale", string.Join(";", j.ResultPaths.Select(Path.GetFileName)));
        mgr.Options.Conflict = ConflictPolicy.Ask;
    }

    if (only is "" or "3")
    {
        Console.WriteLine("== T4 limiti");
        mgr.Options.Conflict = ConflictPolicy.Overwrite;
        mgr.Options.MaxFileBytes = 1024 * 1024;
        await Mk(D); await Mk(D + "/limite"); await Mk(D + "/limite2");
        var j = await Run(mgr.EnqueueUpload(new[] { Path.Combine(work, "ft_src") }, D + "/limite"));
        Check("T4 file oltre 1 MB saltati", j.State == TransferState.CompletedWithSkips && j.Items.Count(i => i.Status.Contains("supera il limite")) == 2, string.Join(" | ", j.Items.Where(i => i.Status.Contains("limite")).Select(i => i.Source)));
        mgr.Options.MaxFileBytes = 0; mgr.Options.MaxJobBytes = 10 * 1024 * 1024;
        j = await Run(mgr.EnqueueUpload(new[] { Path.Combine(work, "ft_src") }, D + "/limite2"));
        Check("T4 limite totale: fallisce prima di inviare", j.State == TransferState.Failed && j.Error?.Kind == FtErrorKind.TooLarge && j.DoneBytes == 0, j.Error?.Message ?? "");
        mgr.Options.MaxJobBytes = 0;
    }

    if (only is "" or "4")
    {
        Console.WriteLine("== T5 annullamento e navigazione durante il trasferimento");
        mgr.Options.Conflict = ConflictPolicy.Overwrite;
        var big = Path.Combine(work, "ft_src", "grande.bin");
        await Mk(D); await Mk(D + "/annulla");
        var j = mgr.EnqueueUpload(new[] { big }, D + "/annulla");
        while (j.State != TransferState.Running || j.DoneBytes < 8 * 1024 * 1024) { await Task.Delay(100); if (j.IsFinished) break; }
        var t0 = Stopwatch.StartNew();
        var listing = await mgr.ListAsync(D);
        Check("T5 si sfoglia mentre si trasferisce", listing.Count > 0 && j.State == TransferState.Running, $"{t0.ElapsedMilliseconds} ms, stato {j.State}");
        j.Cancel();
        await Run(j);
        Check("T5 stato Cancelled", j.State == TransferState.Cancelled, j.State.ToString());
        await Task.Delay(500);
        var left = (await mgr.ListAsync(D + "/annulla")).Select(e => e.Name).ToList();
        Check("T5 nessun file a meta' sul remoto", left.Count == 0, string.Join(",", left));
    }

    if (only is "" or "6")
    {
        Console.WriteLine("== T7 lettura al volo (file virtuali)");
        mgr.Options.Conflict = ConflictPolicy.Overwrite;
        await Mk(D);
        var big3 = Path.Combine(work, "ft_src", "Cartella è€", "sub", "日本語.bin");
        var j = await Run(mgr.EnqueueUpload(new[] { big3, Path.Combine(work, "ft_src", "vuoto.txt") }, D));
        var ents = await mgr.ListAsync(D);
        await using (var s = await mgr.OpenReadStreamAsync(ents.First(e => e.Name == "日本語.bin")))
        {
            var sha = Convert.ToHexString(SHA256.HashData(s))[..12];
            Check("T7 stream: contenuto identico", sha == Sha(big3), sha);
        }
        await using (var s = await mgr.OpenReadStreamAsync(ents.First(e => e.Name == "vuoto.txt")))
        {
            var ms = new MemoryStream(); await s.CopyToAsync(ms);
            Check("T7 stream di file vuoto", ms.Length == 0);
        }
        // chiusura anticipata: non deve restare niente di appeso
        var s2 = await mgr.OpenReadStreamAsync(ents.First(e => e.Name == "日本語.bin"));
        var buf = new byte[1000]; await s2.ReadAsync(buf); s2.Dispose();
        await Task.Delay(1500);
        var again = await mgr.ListAsync(D);
        Check("T7 chiusura anticipata senza blocchi", again.Count >= 2);
    }

    if (only is "" or "5")
    {
        Console.WriteLine("== T6 gestione remoto");
        await Mk(D);
        await Mk(D + "/ft_src"); await Mk(D + "/ft_src/x"); await mgr.EnqueueUpload(new[] { Path.Combine(work, "ft_src", "a.txt") }, D + "/ft_src/x").Completion;
        await mgr.MkdirAsync(D + "/nuova è");
        await mgr.RenameAsync(D + "/nuova è", D + "/rinominata è");
        var names = (await mgr.ListAsync(D)).Select(e => e.Name).ToList();
        Check("T6 mkdir + rinomina", names.Contains("rinominata è") && !names.Contains("nuova è"));
        var tree = (await mgr.ListAsync(D)).First(e => e.Name == "ft_src");
        await mgr.DeleteAsync(tree);
        names = (await mgr.ListAsync(D)).Select(e => e.Name).ToList();
        Check("T6 cancellazione ricorsiva di una cartella piena", !names.Contains("ft_src"));
        try { await mgr.MkdirAsync("/C:/Windows/provalemora_x"); Check("T6 cartella protetta rifiutata", false, "creata!"); await mgr.DeleteAsync(new RemoteEntry("/C:/Windows", "provalemora_x", true, 0, DateTime.MinValue)); }
        catch (FileTransferException e) { Check("T6 cartella protetta rifiutata", e.Kind == FtErrorKind.Denied, $"{e.Kind}: {e.Message}"); }
    }
}
catch (Exception e) { Console.WriteLine("ECCEZIONE NON GESTITA: " + e); fail++; }
finally
{
    try { var rest = (await mgr.ListAsync("/C:")).FirstOrDefault(e => e.Name == "provalemora"); if (rest != null) await mgr.DeleteAsync(rest); Console.WriteLine("pulizia remota fatta"); } catch (Exception e) { Console.WriteLine("pulizia remota non riuscita: " + e.Message); }
    mgr.Dispose();
    try { Directory.Delete(work, true); } catch { }
}
Console.WriteLine($"\nRISULTATO: {pass} riusciti, {fail} falliti");
