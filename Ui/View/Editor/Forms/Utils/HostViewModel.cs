using System;
using System.ComponentModel;
using System.Linq;
using Lemora.Model.Protocol.Base;
using Lemora.Service;
using Lemora.Utils;
using Shawn.Utils.Wpf;

namespace Lemora.View.Editor.Forms.Utils;

/// <summary>
/// Server address and port. When the folder has an IP prefix (the site network, e.g. "192.0.2.")
/// the prefix appears as a chip to the left of the field and only the final value is typed; the full address stays in New.Address.
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
    /// <summary>active IP prefix (empty = none)</summary>
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

    /// <summary>a complete address (IP with at least 3 dots, or a name with letters) does not use the prefix</summary>
    public static bool IsCompleteAddress(string v) => v.Any(char.IsLetter) || v.Count(c => c == '.') >= 3;

    /// <summary>field text: without a prefix it is the address, with a prefix it is only the final part</summary>
    public string AddressInput
    {
        get => HasIpPrefix && New.Address.StartsWith(_ipPrefix, StringComparison.Ordinal) ? New.Address.Substring(_ipPrefix.Length) : New.Address;
        set
        {
            var v = (value ?? "").Trim();
            if (HasIpPrefix)
            {
                if (IsCompleteAddress(v)) { IpPrefix = ""; New.Address = v; }   // the prefix goes away by itself
                else New.Address = _ipPrefix + v;
            }
            else
            {
                New.Address = v;
            }
            RaisePropertyChanged();
        }
    }

    /// <summary>true if nothing has been typed (with the prefix active, the address is just the prefix)</summary>
    public bool IsAddressEmpty => string.IsNullOrWhiteSpace(New.Address) || (HasIpPrefix && New.Address == _ipPrefix);

    private RelayCommand? _cmdRemovePrefix;
    /// <summary>✕ on the chip: removes the prefix and leaves the full address in the field</summary>
    public RelayCommand CmdRemovePrefix => _cmdRemovePrefix ??= new RelayCommand(o =>
    {
        if (New.Address == _ipPrefix) New.Address = "";
        IpPrefix = "";
    });

    /// <summary>
    /// Changes the folder: recomputes the prefix. Returns true if the active prefix changed.
    /// The typed address is NEVER rewritten: only how it is displayed changes (chip + final part, or the whole address).
    /// Only the "empty" prefix (no final part typed) is replaced, as in a new entry.
    /// Before, the address 10.0.0.6 shown as chip "10.0.0." + "6", moved into a folder without a prefix, became "6".
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
            // nothing typed: suggest the new folder's prefix
            IpPrefix = newPrefix;
            New.Address = newPrefix;
        }
        else if (newPrefix.Length > 0 && address.StartsWith(newPrefix, StringComparison.Ordinal) && !IsCompleteAddress(address.Substring(newPrefix.Length)))
        {
            IpPrefix = newPrefix;   // same address, shown as chip + final part
        }
        else
        {
            IpPrefix = "";   // address shown in full
        }
        RaisePropertyChanged(nameof(AddressInput));
        return !string.Equals(previous, _ipPrefix, StringComparison.Ordinal);
    }

    /// <summary>undo: restores the prefix and the address as they were</summary>
    public void Restore(string ipPrefix, string address)
    {
        _ipPrefix = ipPrefix;
        New.Address = address;
        RaisePropertyChanged(nameof(IpPrefix));
        RaisePropertyChanged(nameof(HasIpPrefix));
        RaisePropertyChanged(nameof(AddressInput));
        RaisePropertyChanged(nameof(IsAddressEmpty));
    }

    // address errors are the server's, shown on the field that contains it
    public string Error => "";
    public string this[string columnName] => columnName == nameof(AddressInput) ? New[nameof(New.Address)] : "";
}
