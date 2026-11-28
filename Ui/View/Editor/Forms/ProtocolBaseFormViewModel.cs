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

        /// <summary>the editor notifies the form when the selected folder changes</summary>
        public virtual void OnFolderChanged(string folderPath) { }

        /// <summary>called by the editor right before saving, when the data is valid</summary>
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
