using System;
using Lemora.View.Utils.MaskAndPop;
using Shawn.Utils.Wpf;

namespace Lemora.View
{
    // il controllo degli aggiornamenti (guardava il sito del progetto originale) e' stato tolto il 2026-11-04: arrivera' un sistema nostro
    public class AboutPageViewModel : PopupBase
    {
        public string CurrentVersion => AppVersion.Version;
        public string CurrentVersionDate => AppVersion.BuildDate.IndexOf("+", StringComparison.Ordinal) > 0 ? AppVersion.BuildDate.Substring(0, AppVersion.BuildDate.LastIndexOf("+", StringComparison.Ordinal)) : AppVersion.BuildDate;

        private RelayCommand? _cmdClose;
        public RelayCommand CmdClose
        {
            get
            {
                return _cmdClose ??= new RelayCommand((o) =>
                {
                    this.RequestClose();
                });
            }
        }
    }
}
