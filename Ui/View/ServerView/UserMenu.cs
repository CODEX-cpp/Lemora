using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Lemora.Model;
using Lemora.Model.Protocol.Base;
using Lemora.Service;

namespace Lemora.View.ServerView
{
    /// <summary>menu dell'utente di una riga (Home o cartelle): sceglie l'utente predefinito del server (o "Chiedi sempre") e lo salva</summary>
    public static class UserMenu
    {
        public static void Show(FrameworkElement anchor, ProtocolBaseWithAddressPortUserPwd server, List<UserResolver.UserChoice> choices, Action onChanged)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom, MinWidth = 200, VerticalOffset = 2 };
            void Save(Action change)
            {
                change();
                if (server.DataSource?.IsWritable == true) IoC.Get<GlobalData>().UpdateServer(server);
                onChanged();
            }
            foreach (var c in choices)
            {
                var choice = c;
                var current = server.AskWhichUser != true && ReferenceEquals(UserResolver.EffectiveChoice(server, choices), choice);
                var mi = new MenuItem { Header = choice.Display, IsChecked = current };
                mi.Click += (_, _) => Save(() => UserResolver.ApplyChoice(server, choice));
                menu.Items.Add(mi);
            }
            menu.Items.Add(new Separator());
            var ask = new MenuItem { Header = IoC.Translate("users_ask"), IsChecked = server.AskWhichUser == true };
            ask.Click += (_, _) => Save(() => server.AskWhichUser = true);
            menu.Items.Add(ask);
            menu.IsOpen = true;
        }
    }
}
