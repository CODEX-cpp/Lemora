using System;
using Newtonsoft.Json;
using Lemora.Model.Protocol.Base;
using Shawn.Utils;

namespace Lemora.Model.Protocol
{
    // ReSharper disable once InconsistentNaming
    public class VNC : ProtocolBaseWithAddressPortUserPwd
    {
        public enum EVncWindowResizeMode
        {
            Stretch = 0,
            Fixed = 1,
        }

        public static string ProtocolName = "VNC";
        public VNC() : base(ProtocolName, "VNC.V1", "VNC")
        {
            base.Port = "5900";
            base.UserName = "";
        }

        private EVncWindowResizeMode? _vncWindowResizeMode = EVncWindowResizeMode.Stretch;

        public EVncWindowResizeMode? VncWindowResizeMode
        {
            get => _vncWindowResizeMode;
            set => SetAndNotifyIfChanged(ref _vncWindowResizeMode, value);
        }

        public enum EVncQuality
        {
            /// <summary>parte bassa (16 bit) e sale ai colori pieni se la linea regge, come RealVNC</summary>
            Auto = 0,
            /// <summary>sempre 16 bit e JPEG basso: meta' dei dati</summary>
            Low = 1,
            /// <summary>sempre il formato del server (colori pieni)</summary>
            High = 2,
        }

        private EVncQuality? _vncQuality = EVncQuality.Auto;   // predefinita per tutte le connessioni (anche quelle salvate senza questo valore)

        public EVncQuality? VncQuality
        {
            get => _vncQuality;
            set => SetAndNotifyIfChanged(ref _vncQuality, value);
        }

        public override bool IsOnlyOneInstance()
        {
            return true;
        }

        public override ProtocolBase? CreateFromJsonString(string jsonString)
        {
            try
            {
                return JsonConvert.DeserializeObject<VNC>(jsonString);
            }
            catch (Exception e)
            {
                SimpleLogHelper.Debug(e);
                return null;
            }
        }

        public override double GetListOrder()
        {
            return 1;
        }

        public override bool ShowUserNameInput()
        {
            return false;
        }

        public override bool ShowPasswordInput()
        {
            return true;
        }

        public override bool ShowPrivateKeyInput()
        {
            return false;
        }
    }
}