using System;
using Lemora.Utils;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.StartPanel;

namespace Lemora.View.Launcher;

public class QuickConnectionItem
{
    public string Protocol { get; set; }= "";
    public string Host { get; set; } = "";
    public string Misc { get; set; } = "";
    /// <summary>last use, to sort the Home history</summary>
    public DateTime LastTime { get; set; } = DateTime.MinValue;
    /// <summary>true if the connection is among the Home favorites (even if it is not a saved server)</summary>
    public bool IsFavorite { get; set; }

    private const string separator = "[:`*_^_*`:]";
    public void SetUserPassword(string username, string password, string privateKeyPath)
    {
        Misc = DataVault.EncryptOnce($"{username}{separator}{password}{separator}{privateKeyPath}{separator}{DateTime.Now.Millisecond}");
    }

    // decryption is slow (~10 ms): Home asks for it for every row on every rebuild, so the last result is cached
    private string? _cachedMisc;
    private (string, string, string) _cached;

    public (string username, string password, string privateKeyPath) GetUserPassword()
    {
        var misc = Misc;
        if (_cachedMisc == misc) return _cached;
        var userPwd = DataVault.DecryptOrReturnOriginalString(misc);
        var parts = userPwd.Split(new[] { separator }, StringSplitOptions.None);
        _cached = parts.Length == 4 ? (parts[0], parts[1], parts[2]) : ("", "", "");
        _cachedMisc = misc;
        return _cached;
    }
}