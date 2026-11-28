using System;
using System.Collections.Generic;

namespace Lemora.Utils.Tracing
{
    /// <summary>error reporting: today only Sentry (and only if a key was put in the app); AppCenter was shut down by Microsoft and has been removed</summary>
    internal static class UnifyTracing
    {
        public static void Init()
        {
            SentryIoHelper.Init(Assert.SENTRY_IO_DEN);
        }

        public static void Error(Exception e, IDictionary<string, string>? properties = null, Dictionary<string, string>? attachments = null)
        {
            // also in Lemora's own log, with the stack: without a Sentry key the error shown to the user left no trace (e.g. "Collection was modified" on save)
            try { Shawn.Utils.SimpleLogHelper.Error(e); } catch { /* the log must never block the error */ }
            SentryIoHelper.Error(e, properties, attachments);
        }

        public static void TraceSpecial(Dictionary<string, string> kys)
        {
            SentryIoHelper.TraceSpecial(kys);
        }

        public static void TraceSessionOpen(string protocol, string via)
        {
            // session-open statistics went only to AppCenter: no destination now
        }
    }
}
