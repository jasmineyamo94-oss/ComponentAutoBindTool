using ComponentAutoBindTool.ViewCore;
using UnityEngine;
using UnityEngine.UI;

public abstract class FlowViewBase : ViewBase
{
    private StageViewRootHelper _stage;
    private GraphicRaycaster _raycaster;

    public override void SetInteractable(bool interactable)
    {
        if (_stage == null)
            _stage = GetComponentInParent<StageViewRootHelper>();
        if (_raycaster == null)
            _raycaster = GetComponent<GraphicRaycaster>();
        if (_stage != null)
            _stage.SetDesiredInteractable(_raycaster, interactable);
        else
            base.SetInteractable(interactable);
    }
}
