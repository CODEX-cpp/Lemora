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
    /// <summary>user menu of a row (Home or folders): picks the server's default user (or "Always ask") and saves it</summary>
    public static class UserMenu
    {
        public static void Show(FrameworkElement anchor, ProtocolBaseWithAddressPortUserPwd server, List<UserResolver.UserChoice> choices, Action onChanged)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom, MinWidth = 200, VerticalOffset = 2 };
            void Save(Action change)
            {
                change();
                // Teams entries: the server does not keep the chosen user (with "with approval" an empty request would be sent): it only lasts until the next sync
                if (server.DataSource?.IsWritable == true && server.DataSource is not Lemora.Service.DataSource.Model.TeamsSource) IoC.Get<GlobalData>().UpdateServer(server);
                onChanged();
            }
            foreach (var c in choices)
            {
                var choice = c;
                var current = server.AskWhichUser != true && ReferenceEquals(UserResolver.EffectiveChoice(server, choices), choice);
                var mi = new MenuItem { Header = choice.Display, IsChecked = current };
                mi.Click += async (_, _) =>
                {
                    // activating a folder credential sends it to the entry's address: same verification as showing or copying it (security hardening)
                    if (choice.IsFolder && !server.ActiveFolderCredentialIds.Contains(choice.Key.Substring(2))
                        && await SecondaryVerificationHelper.VerifyAsyncUi() != true) return;
                    Save(() => UserResolver.ApplyChoice(server, choice));
                };
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
