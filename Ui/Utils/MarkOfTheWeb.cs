using System;
using System.IO;
using Shawn.Utils;

namespace Lemora.Utils
{
    /// <summary>
    /// "downloaded from the Internet" mark (Zone.Identifier, zone 3) on files taken from a remote PC or server: this way Windows and Office
    /// treat the file like an attachment (SmartScreen, Protected View, blocked macros). Security hardening.
    /// </summary>
    public static class MarkOfTheWeb
    {
        public static void Apply(string path, string? source = null)
        {
            try
            {
                var text = "[ZoneTransfer]\r\nZoneId=3\r\n";
                if (!string.IsNullOrWhiteSpace(source) && source!.IndexOfAny(new[] { '\r', '\n' }) < 0) text += "HostUrl=" + source + "\r\n";
                File.WriteAllText(path + ":Zone.Identifier", text);
            }
            catch (Exception e)
            {
                // non-NTFS disk (FAT, some shares): the mark cannot be written
                SimpleLogHelper.Warning("Mark-of-the-Web not written: " + e.Message);
            }
        }

        public static bool Has(string path) => File.Exists(path + ":Zone.Identifier");

        /// <summary>Windows zone of the file (3 = Internet, 4 = restricted sites); 0 if there is no mark or it cannot be read</summary>
        public static int Zone(string path)
        {
            try
            {
                foreach (var line in File.ReadAllLines(path + ":Zone.Identifier"))
                    if (line.StartsWith("ZoneId=", StringComparison.OrdinalIgnoreCase) && int.TryParse(line.Substring(7).Trim(), out var z)) return z;
            }
            catch { /* no mark */ }
            return 0;
        }
    }
}
