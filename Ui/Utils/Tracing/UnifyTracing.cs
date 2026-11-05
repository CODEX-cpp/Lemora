using System;
using System.Collections.Generic;

namespace Lemora.Utils.Tracing
{
    /// <summary>segnalazione errori: oggi solo Sentry (e solo se nell'app e' stata messa una chiave); AppCenter e' stato chiuso da Microsoft ed e' stato tolto</summary>
    internal static class UnifyTracing
    {
        public static void Init()
        {
            SentryIoHelper.Init(Assert.SENTRY_IO_DEN);
        }

        public static void Error(Exception e, IDictionary<string, string>? properties = null, Dictionary<string, string>? attachments = null)
        {
            SentryIoHelper.Error(e, properties, attachments);
        }

        public static void TraceSpecial(Dictionary<string, string> kys)
        {
            SentryIoHelper.TraceSpecial(kys);
        }

        public static void TraceSessionOpen(string protocol, string via)
        {
            // le statistiche di apertura sessione andavano solo ad AppCenter: nessuna destinazione
        }
    }
}
