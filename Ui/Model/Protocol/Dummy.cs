using System;
using Newtonsoft.Json;
using Lemora.Model.Protocol.Base;
using Shawn.Utils;

namespace Lemora.Model.Protocol
{
    public class Dummy : ProtocolBase
    {
        public static string ProtocolName = "";
        public Dummy() : base(ProtocolName, "", "")
        {
        }

        public override bool IsOnlyOneInstance()
        {
            return true;
        }

        public override ProtocolBase? CreateFromJsonString(string jsonString)
        {
            return null;
        }

        protected override string GetSubTitle()
        {
            return "";
        }

        public override double GetListOrder()
        {
            return -1;
        }
    }
}