using System.Windows;

namespace Lemora.View.Utils;

/****
 Mask layer controller IMaskLayerContainer MaskLayerContainerScreenBase

 Mask layer container MaskLayerController

 Mask layer: MaskLayer

 A mask layer container (window view) inherits MaskLayerContainerScreenBase and, once started, subscribes to MaskLayerController.ProcessingRingInvoke
 MaskLayerController.OnShowProcessingRing alone controls the mask display of all mask layer containers
 */

public interface IMaskLayerContainer
{
    public MaskLayer? TopLevelViewModel { get; set; }

    void OnShowProcessingRing(long layerId, Visibility visibility, string msg);
    void ShowOrHideMask(long layerId, MaskLayer? layer);
}