using System;
using System.ComponentModel;
using System.Linq;
using Lemora.Model.Protocol.Base;
using Lemora.Service;
using Lemora.Utils;
using Shawn.Utils.Wpf;

namespace Lemora.View.Editor.Forms.Utils;

/// <summary>
/// Indirizzo e porta del server. Quando la cartella ha un prefisso IP (la rete della sede, es. "192.168.100.")
/// il prefisso appare come chip a sinistra del campo e si scrive solo il valore finale; l'indirizzo completo resta in New.Address.
/// </summary>
public class HostViewModel : NotifyPropertyChangedBaseScreen, IDataErrorInfo
{
    public ProtocolBaseWithAddressPort New { get; }
    public HostViewModel(ProtocolBaseWithAddressPort protocol)
    {
        New = protocol;
        New.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(New.Address))
            {
                RaisePropertyChanged(nameof(AddressInput));
                RaisePropertyChanged(nameof(IsAddressEmpty));
            }
        };
    }

    private string _ipPrefix = "";
    /// <summary>prefisso IP attivo (vuoto = nessuno)</summary>
    public string IpPrefix
    {
        get => _ipPrefix;
        private set
        {
            if (!SetAndNotifyIfChanged(ref _ipPrefix, value)) return;
            RaisePropertyChanged(nameof(HasIpPrefix));
            RaisePropertyChanged(nameof(AddressInput));
            RaisePropertyChanged(nameof(IsAddressEmpty));
        }
    }
    public bool HasIpPrefix => _ipPrefix.Length > 0;

    private string _folderName = "";
    public string FolderName { get => _folderName; private set => SetAndNotifyIfChanged(ref _folderName, value); }

    public string PrefixHint => string.Format(IoC.Translate("host_prefix_hint"), FolderName);

    /// <summary>un indirizzo completo (IP con almeno 3 punti, oppure un nome con lettere) non usa il prefisso</summary>
    public static bool IsCompleteAddress(string v) => v.Any(char.IsLetter) || v.Count(c => c == '.') >= 3;

    /// <summary>testo del campo: senza prefisso e' l'indirizzo, con prefisso e' solo la parte finale</summary>
    public string AddressInput
    {
        get => HasIpPrefix && New.Address.StartsWith(_ipPrefix, StringComparison.Ordinal) ? New.Address.Substring(_ipPrefix.Length) : New.Address;
        set
        {
            var v = (value ?? "").Trim();
            if (HasIpPrefix)
            {
                if (IsCompleteAddress(v)) { IpPrefix = ""; New.Address = v; }   // il prefisso sparisce da solo
                else New.Address = _ipPrefix + v;
            }
            else
            {
                New.Address = v;
            }
            RaisePropertyChanged();
        }
    }

    /// <summary>true se non e' stato scritto niente (con il prefisso attivo l'indirizzo e' solo il prefisso)</summary>
    public bool IsAddressEmpty => string.IsNullOrWhiteSpace(New.Address) || (HasIpPrefix && New.Address == _ipPrefix);

    private RelayCommand? _cmdRemovePrefix;
    /// <summary>✕ sul chip: toglie il prefisso e lascia nel campo l'indirizzo completo</summary>
    public RelayCommand CmdRemovePrefix => _cmdRemovePrefix ??= new RelayCommand(o =>
    {
        if (New.Address == _ipPrefix) New.Address = "";
        IpPrefix = "";
    });

    /// <summary>
    /// Cambia la cartella: ricalcola il prefisso. Restituisce true se il prefisso attivo e' cambiato.
    /// L'indirizzo scritto non viene MAI riscritto: cambia solo come si mostra (chip + parte finale, oppure indirizzo intero).
    /// Si sostituisce solo il prefisso "vuoto" (nessuna parte finale scritta), come in una voce nuova.
    /// Prima l'indirizzo 10.0.0.6 mostrato come chip "10.0.0." + "6", spostato in una cartella senza prefisso, diventava "6".
    /// </summary>
    public bool SetFolder(string folderPath)
    {
        var previous = _ipPrefix;
        FolderName = FolderService.Leaf(folderPath);
        RaisePropertyChanged(nameof(PrefixHint));
        var newPrefix = string.IsNullOrEmpty(folderPath) ? "" : FolderService.EffectiveIpPrefix(folderPath);
        var address = New.Address ?? "";

        if (address.Length == 0 || (previous.Length > 0 && address == previous))
        {
            // niente di scritto: si propone il prefisso della nuova cartella
            IpPrefix = newPrefix;
            New.Address = newPrefix;
        }
        else if (newPrefix.Length > 0 && address.StartsWith(newPrefix, StringComparison.Ordinal) && !IsCompleteAddress(address.Substring(newPrefix.Length)))
        {
            IpPrefix = newPrefix;   // stesso indirizzo, mostrato come chip + parte finale
        }
        else
        {
            IpPrefix = "";   // indirizzo mostrato per intero
        }
        RaisePropertyChanged(nameof(AddressInput));
        return !string.Equals(previous, _ipPrefix, StringComparison.Ordinal);
    }

    /// <summary>annulla: rimette il prefisso e l'indirizzo com'erano</summary>
    public void Restore(string ipPrefix, string address)
    {
        _ipPrefix = ipPrefix;
        New.Address = address;
        RaisePropertyChanged(nameof(IpPrefix));
        RaisePropertyChanged(nameof(HasIpPrefix));
        RaisePropertyChanged(nameof(AddressInput));
        RaisePropertyChanged(nameof(IsAddressEmpty));
    }

    // gli errori dell'indirizzo sono quelli del server, mostrati sul campo che lo contiene
    public string Error => "";
    public string this[string columnName] => columnName == nameof(AddressInput) ? New[nameof(New.Address)] : "";
}
