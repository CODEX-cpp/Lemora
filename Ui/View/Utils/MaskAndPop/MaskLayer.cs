using Shawn.Utils;

namespace Lemora.View.Utils;

public abstract class MaskLayer : NotifyPropertyChangedBase
{
    public long LayerId { get; set; }

    /// <summary>
    /// Tells whether the current mask should be removed
    /// </summary>
    /// <param name="latestLayerId"></param>
    /// <returns></returns>
    public bool IsNeedRemoval(long latestLayerId)
    {
        return latestLayerId >= LayerId;
    }
}