using System.Linq;
using VncSharpCore;
using Xunit;

namespace Tests.Service
{
    /// <summary>rotellina del mouse e touchpad: uno scatto ogni 120 di spostamento, non uno scatto per evento</summary>
    public class WheelAccumulatorTests
    {
        [Fact]
        public void RealMouseWheel_EveryNotchPassesOnce()
        {
            var w = new WheelAccumulator();
            for (var i = 0; i < 5; i++) Assert.Equal(1, w.Add(120, i * 10));   // scatti veloci: nessuno si perde
            for (var i = 0; i < 5; i++) Assert.Equal(1, w.Add(-120, 1000 + i * 10));
        }

        [Fact]
        public void Touchpad_SmallEventsAddUpToNotches()
        {
            var w = new WheelAccumulator();
            var total = Enumerable.Range(0, 20).Sum(i => w.Add(30, i * 8));   // 20 eventi da 30 = 600 = 5 scatti (prima ne passavano 20)
            Assert.Equal(5, total);
        }

        [Fact]
        public void Touchpad_OneSmallEventDoesNothing()
        {
            Assert.Equal(0, new WheelAccumulator().Add(30, 0));
        }

        [Fact]
        public void Pause_DropsTheLeftover()
        {
            var w = new WheelAccumulator();
            Assert.Equal(0, w.Add(90, 0));
            Assert.Equal(0, w.Add(90, 1000));   // dopo una pausa il 90 di prima non conta: 90 da solo non basta
        }

        [Fact]
        public void ChangeOfDirection_StartsAgain()
        {
            var w = new WheelAccumulator();
            Assert.Equal(0, w.Add(100, 0));
            Assert.Equal(0, w.Add(-100, 10));   // senza reset farebbe 0 con resto 0 ma mai uno scatto all'indietro per i 100 di prima
            Assert.Equal(1, w.Add(-30, 20));    // -100 + -30 = uno scatto indietro
        }

        [Fact]
        public void HugeDelta_IsCappedPerEvent()
        {
            Assert.Equal(WheelAccumulator.MaxStepsPerEvent, new WheelAccumulator().Add(120 * 30, 0));
        }
    }
}
