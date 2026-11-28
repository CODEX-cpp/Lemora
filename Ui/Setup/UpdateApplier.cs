using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using Shawn.Utils;

namespace Lemora.Setup
{
    /// <summary>
    /// Automatic update, part 2: the installation folder has this layout
    ///   Lemora.exe            the launcher (small, almost never changes): shortcuts and start with Windows point here
    ///   current.txt           the version to start, for example 1.3.1.11105
    ///   versions\1.3.1.11105\Lemora.exe   the real app, one folder per version
    /// An update adds a folder and changes current.txt: it never overwrites a file in use.
    /// </summary>
    internal static class UpdateApplier
    {
        public const string VersionsDir = "versions";
        public const string CurrentFile = "current.txt";
        public const string AppExe = "Lemora.exe";

        /// <summary>the compiled launcher (Installer\launcher), embedded in the exe: the installation writes it as Lemora.exe in the main folder</summary>
        public static byte[]? LauncherBytes()
        {
            try
            {
                var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Resources/Launcher/Lemora-launcher.bin"));
                if (info == null) return null;
                using var s = info.Stream;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
            catch (Exception e) when (e is IOException or UriFormatException or InvalidOperationException) { return null; }
        }

        /// <summary>installation folder if this exe sits inside "versions\<version>\", otherwise null (arbitrary exe or old "flat" installation)</summary>
        public static string? RootOfVersionedExe(string exePath)
        {
            var versionDir = Path.GetDirectoryName(exePath);
            var versions = versionDir == null ? null : Path.GetDirectoryName(versionDir);
            if (versions == null || !string.Equals(Path.GetFileName(versions), VersionsDir, StringComparison.OrdinalIgnoreCase)) return null;
            return Path.GetDirectoryName(versions);
        }

        /// <summary>
        /// Puts a version in <c>versions\&lt;version&gt;</c> and makes it the current one.
        /// <paramref name="zip"/> is the package (Lemora.exe at the zip root), already in memory: the caller has just verified it
        /// on the same bytes, so nobody can change the file between the check and the use. Work happens in a ".tmp" folder that is then renamed:
        /// if something is interrupted, a half-written version is never left selected as current.
        /// </summary>
        /// <param name="checkVersion">real update (elevated copy): the exe in the package must have the announced version, newer than the one in use</param>
        public static void ActivateFromZip(byte[] zip, string installDir, string version, bool checkVersion = false)
        {
            if (!System.Version.TryParse(version, out var parsed) || parsed.ToString() != version) throw new ArgumentException("Invalid version: " + version);
            var versions = Path.Combine(installDir, VersionsDir);
            var final = Path.Combine(versions, version);
            var tmp = final + ".tmp";
            Directory.CreateDirectory(versions);
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            ExtractSafely(zip, tmp);
            if (!File.Exists(Path.Combine(tmp, AppExe))) { Directory.Delete(tmp, true); throw new InvalidDataException($"The package does not contain {AppExe} at its root"); }
            // the signature covers the package bytes, not the declared version: the real version is that of the exe INSIDE the signed package.
            // It must be the announced one and newer than the one in use: no going back to an old signed release (with known flaws)
            if (checkVersion && DowngradeOrMismatch(Path.Combine(tmp, AppExe), parsed, RunningVersion) is { } why) { Directory.Delete(tmp, true); throw new InvalidDataException(why); }
            if (Directory.Exists(final)) Directory.Delete(final, true);
            Directory.Move(tmp, final);
            SetCurrent(installDir, version);
            Prune(installDir, version);
        }

        /// <summary>null if fine; otherwise the reason. <paramref name="running"/> = version in use (the elevated copy is the current version)</summary>
        internal static string? DowngradeOrMismatch(string exe, Version announced, string running)
        {
            var fv = FileVersionInfo.GetVersionInfo(exe).FileVersion;
            if (!Version.TryParse(fv, out var inside)) return "program version in the package is not readable";
            if (inside != announced) return $"the package contains version {inside}, not the announced {announced}";
            if (Version.TryParse(running, out var cur) && inside <= cur) return $"version {inside} is not newer than the installed one ({cur}): update rejected";
            return null;
        }

        /// <summary>extracts the zip, rejecting paths that escape the destination folder (zip-slip)</summary>
        private static void ExtractSafely(byte[] zipBytes, string dest)
        {
            Directory.CreateDirectory(dest);
            var root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
            using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            foreach (var e in zip.Entries)
            {
                var target = Path.GetFullPath(Path.Combine(dest, e.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The package contains a path that is not allowed: " + e.FullName);
                if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                e.ExtractToFile(target, true);
            }
        }

        public static string? ReadCurrent(string installDir)
        {
            try { return File.ReadAllText(Path.Combine(installDir, CurrentFile)).Trim() is { Length: > 0 } v ? v : null; }
            catch (IOException) { return null; }
        }

        /// <summary>current.txt is written to a temporary file and swapped in: the launcher never finds it half-written</summary>
        public static void SetCurrent(string installDir, string version)
        {
            var file = Path.Combine(installDir, CurrentFile);
            var tmp = file + ".new";
            File.WriteAllText(tmp, version);
            File.Move(tmp, file, true);
        }

        /// <summary>keeps the current version and the most recent of the others (safety net); deletes the rest and the ".tmp" folders left half-done</summary>
        public static void Prune(string installDir, string current)
        {
            var versions = Path.Combine(installDir, VersionsDir);
            if (!Directory.Exists(versions)) return;
            foreach (var t in Directory.GetDirectories(versions, "*.tmp")) TryDelete(t);
            var others = Directory.GetDirectories(versions)
                .Where(d => !string.Equals(Path.GetFileName(d), current, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => Directory.GetCreationTimeUtc(d)).Skip(1);
            foreach (var d in others) TryDelete(d);   // a version still in use cannot be deleted: it stays and goes at the next cleanup
        }

        private static void TryDelete(string dir)
        {
            try { Directory.Delete(dir, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SimpleLogHelper.Debug("Old version not deleted: " + e.Message); }
        }

        /// <summary>version of the running app, as a folder name</summary>
        public static string RunningVersion => (Assembly.GetEntryAssembly() ?? typeof(UpdateApplier).Assembly).GetName().Version?.ToString() ?? "0.0.0.0";
    }
}
