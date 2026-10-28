using UnityEngine;

namespace Third_Party.ComponentAutoBindTool.Scripts.ViewCore
{
    public class ViewRootHelper : MonoBehaviour, IViewRootHelper
    {
        public Transform GetViewRoot()
        {
            return transform;
        }
    }
}
