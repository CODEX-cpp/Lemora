using System;

namespace VncSharpCore
{
    /// <summary>
    /// Turns wheel movements into notches. A real wheel sends 120 at a time (one notch); a touchpad sends many small
    /// events (10-40): counting each one as a notch scrolled far too much. Here the movements are summed and one notch is delivered
    /// every 120; the remainder waits, but is reset after a pause or if the direction changes.
    /// </summary>
    public sealed class WheelAccumulator
    {
        public const int NotchDelta = 120;
        public const int MaxStepsPerEvent = 10;
        public const int ResetAfterMs = 400;

        private int _accum;
        private long _lastMs = long.MinValue / 2;

        /// <returns>how many notches (always >= 0) to send now; the direction is the sign of <paramref name="delta"/></returns>
        public int Add(int delta, long nowMs)
        {
            if (delta == 0) return 0;
            if (nowMs - _lastMs > ResetAfterMs || Math.Sign(_accum) != Math.Sign(delta)) _accum = 0;
            _lastMs = nowMs;
            _accum += delta;
            var steps = Math.Min(MaxStepsPerEvent, Math.Abs(_accum) / NotchDelta);
            _accum -= Math.Sign(_accum) * steps * NotchDelta;
            return steps;
        }
    }
}
