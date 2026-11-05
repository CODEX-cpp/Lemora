using Shawn.Utils;
using System;

namespace Lemora.Model
{
    /// <summary>categoria dell'azione, usata per raggruppare i pulsanti nel pannello dettaglio</summary>
    public enum ActionKind { Connect, Edit, Duplicate, Delete, Copy, Export, Other }

    public class ProtocolAction : NotifyPropertyChangedBase
    {
        public string ActionName { get; }
        public ActionKind Kind { get; set; } = ActionKind.Other;

        private readonly Action _action;

        public void Run()
        {
            _action?.Invoke();
        }

        public ProtocolAction(string actionName, Action action)
        {
            ActionName = actionName;
            _action = action;
        }
    }
}
