using System;
using Lemora.Utils;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.StartPanel;

namespace Lemora.View.Launcher;

public class QuickConnectionItem
{
    public string Protocol { get; set; }= "";
    public string Host { get; set; } = "";
    public string Misc { get; set; } = "";
    /// <summary>ultimo uso, per ordinare la cronologia della home</summary>
    public DateTime LastTime { get; set; } = DateTime.MinValue;
    /// <summary>true se la connessione e' tra i preferiti della home (anche se non e' un server salvato)</summary>
    public bool IsFavorite { get; set; }

    private const string separator = "[:`*_^_*`:]";
    public void SetUserPassword(string username, string password, string privateKeyPath)
    {
        Misc = DataVault.EncryptOnce($"{username}{separator}{password}{separator}{privateKeyPath}{separator}{DateTime.Now.Millisecond}");
    }

    // la decifratura e' lenta (~10 ms): la home la chiede per ogni riga a ogni ricostruzione, quindi si ricorda l'ultimo risultato
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