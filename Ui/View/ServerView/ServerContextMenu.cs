using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Lemora.Model;

namespace Lemora.View.ServerView
{
    /// <summary>
    /// menu del tasto destro su una voce, uguale in List, Card e Albero: Connetti e le azioni del pannello dettagli
    /// (che gia' tengono conto del permesso: senza scrittura niente Modifica, Duplica, Elimina, Copia utente/password)
    /// </summary>
    public static class ServerContextMenu
    {
        public static ContextMenu Show(FrameworkElement anchor, ProtocolBaseViewModel vm)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.MousePoint, MinWidth = 200 };
            var connect = new MenuItem { Header = IoC.Translate("Connect") };
            connect.Click += (_, _) => vm.CmdConnServer?.Execute();
            menu.Items.Add(connect);
            var actions = vm.GetActions();
            ActionKind? last = ActionKind.Connect;
            foreach (var a in actions)
            {
                // un separatore tra un gruppo e l'altro (connessione, modifica, copia, altro), mai in fondo
                var group = a.Kind is ActionKind.Edit or ActionKind.Duplicate or ActionKind.Delete ? ActionKind.Edit : a.Kind;
                if (group != last) menu.Items.Add(new Separator());
                last = group;
                var action = a;
                var mi = new MenuItem { Header = action.ActionName };
                if (action.Kind == ActionKind.Delete) mi.Tag = "danger";
                mi.Click += (_, _) => action.Run();
                menu.Items.Add(mi);
            }
            menu.IsOpen = true;
            return menu;
        }
    }
}
