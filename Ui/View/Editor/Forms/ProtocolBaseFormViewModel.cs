using System.ComponentModel;
using Lemora.Model.Protocol.Base;
using Lemora.Utils;
using Newtonsoft.Json;

namespace Lemora.View.Editor.Forms
{
    public class ProtocolBaseFormViewModel : NotifyPropertyChangedBaseScreen, IDataErrorInfo
    {
        public ProtocolBase New { get; }
        public ProtocolBaseFormViewModel(ProtocolBase protocolBase)
        {
            New = protocolBase;
        }

        ~ProtocolBaseFormViewModel()
        {
        }

        /// <summary>l'editor avvisa il form quando cambia la cartella scelta</summary>
        public virtual void OnFolderChanged(string folderPath) { }

        /// <summary>chiamato dall'editor appena prima di salvare, quando i dati sono validi</summary>
        public virtual void BeforeSave() { }

        public virtual bool CanSave()
        {
            if (!string.IsNullOrEmpty(New[nameof(New.DisplayName)]))
                return false;
            return true;
        }

        #region IDataErrorInfo
        [JsonIgnore] public string Error => "";

        [JsonIgnore]
        public virtual string this[string columnName] => New[columnName];

        #endregion
    }
}
