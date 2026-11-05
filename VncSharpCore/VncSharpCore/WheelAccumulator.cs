using System;

namespace VncSharpCore
{
    /// <summary>
    /// Trasforma gli spostamenti della rotellina in scatti. Una rotellina vera manda 120 per volta (uno scatto); un touchpad manda tanti eventi
    /// piccoli (10-40): ognuno contato come uno scatto faceva scorrere molto troppo. Qui gli spostamenti si sommano e si consegna uno scatto
    /// ogni 120; il resto aspetta, ma si azzera dopo una pausa o se cambia direzione.
    /// </summary>
    public sealed class WheelAccumulator
    {
        public const int NotchDelta = 120;
        public const int MaxStepsPerEvent = 10;
        public const int ResetAfterMs = 400;

        private int _accum;
        private long _lastMs = long.MinValue / 2;

        /// <returns>quanti scatti (sempre >= 0) mandare ora; la direzione e' quella del segno di <paramref name="delta"/></returns>
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
