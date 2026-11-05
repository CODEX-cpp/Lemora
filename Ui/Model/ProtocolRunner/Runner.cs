using System;
using System.IO;
using System.Linq;
using JsonKnownTypes;
using Newtonsoft.Json;
using Lemora.Model.ProtocolRunner.Default;
using Lemora.Service;
using Shawn.Utils;

namespace Lemora.Model.ProtocolRunner
{
    [JsonConverter(typeof(JsonKnownTypesConverter<Runner>))] // json serialize/deserialize derived types https://stackoverflow.com/a/60296886/8629624
    [JsonKnownType(typeof(Runner), nameof(Runner))]
    [JsonKnownType(typeof(ExternalRunner), nameof(ExternalRunner))]
    [JsonKnownType(typeof(InternalDefaultRunner), nameof(InternalDefaultRunner))]
#pragma warning disable CS0612 // KittyRunner e' obsoleto ma va riconosciuto per leggere le configurazioni salvate in passato
    [JsonKnownType(typeof(KittyRunner), nameof(KittyRunner))]
#pragma warning restore CS0612
    [JsonKnownType(typeof(PuttyRunner), nameof(PuttyRunner))]
    [JsonKnownType(typeof(TerminalRunner), nameof(TerminalRunner))]
    public class Runner : NotifyPropertyChangedBase, ICloneable
    {
        public Runner(string runnerName, string ownerProtocolName)
        {
            OwnerProtocolName = ownerProtocolName;
            _name = runnerName?.Trim() ?? "";
        }

        protected string _name;
        public string Name
        {
            get => _name;
            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    _name = "";
                    return;
                }
                var str = value;
                var invalid = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
                str = invalid.Aggregate(str, (current, c) => current.Replace(c.ToString(), ""));
                SetAndNotifyIfChanged(ref _name, str);
            }
        }

        public string OwnerProtocolName { get; set; }

        public object Clone()
        {
            return this.MemberwiseClone();
        }
    }
}
