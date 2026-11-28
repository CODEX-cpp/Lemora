using System.Linq;
using VncSharpCore;
using Xunit;

namespace Tests.Service
{
    /// <summary>mouse wheel and touchpad: one notch per 120 of movement, not one notch per event</summary>
    public class WheelAccumulatorTests
    {
        [Fact]
        public void RealMouseWheel_EveryNotchPassesOnce()
        {
            var w = new WheelAccumulator();
            for (var i = 0; i < 5; i++) Assert.Equal(1, w.Add(120, i * 10));   // fast notches: none is lost
            for (var i = 0; i < 5; i++) Assert.Equal(1, w.Add(-120, 1000 + i * 10));
        }

        [Fact]
        public void Touchpad_SmallEventsAddUpToNotches()
        {
            var w = new WheelAccumulator();
            var total = Enumerable.Range(0, 20).Sum(i => w.Add(30, i * 8));   // 20 events of 30 = 600 = 5 notches (previously 20 got through)
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
            Assert.Equal(0, w.Add(90, 1000));   // after a pause the earlier 90 does not count: 90 alone is not enough
        }

        [Fact]
        public void ChangeOfDirection_StartsAgain()
        {
            var w = new WheelAccumulator();
            Assert.Equal(0, w.Add(100, 0));
            Assert.Equal(0, w.Add(-100, 10));   // without a reset it would give 0 with remainder 0, but never a backward notch for the earlier 100
            Assert.Equal(1, w.Add(-30, 20));    // -100 + -30 = one notch backward
        }

        [Fact]
        public void HugeDelta_IsCappedPerEvent()
        {
            Assert.Equal(WheelAccumulator.MaxStepsPerEvent, new WheelAccumulator().Add(120 * 30, 0));
        }
    }
}
