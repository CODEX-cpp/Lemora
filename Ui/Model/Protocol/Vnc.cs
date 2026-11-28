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
            /// <summary>starts low (16 bit) and moves up to full color if the link can take it, like RealVNC</summary>
            Auto = 0,
            /// <summary>always 16 bit and low JPEG: half the data</summary>
            Low = 1,
            /// <summary>always the server's format (full color)</summary>
            High = 2,
        }

        private EVncQuality? _vncQuality = EVncQuality.Auto;   // default for all connections (including those saved without this value)

        public EVncQuality? VncQuality
        {
            get => _vncQuality;
            set => SetAndNotifyIfChanged(ref _vncQuality, value);
        }

        private string _keyOverrides = "";
        /// <summary>keys changed by hand for this connection only ("win=0;altTab=1", see KeyMap); the others follow the VNC defaults</summary>
        public string KeyOverrides
        {
            get => _keyOverrides;
            set => SetAndNotifyIfChanged(ref _keyOverrides, value ?? "");
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