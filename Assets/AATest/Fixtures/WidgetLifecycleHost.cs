#if UNITY_EDITOR
using ComponentAutoBindTool.ViewCore;
using UnityEngine;

namespace ComponentAutoBindTool.Tests
{
    public sealed class WidgetLifecycleHost : WidgetViewBase
    {
        public object UIView { get; set; }

        public override void EnsureAutoBind(GameObject go)
        {
        }

        public override object GetGeneratedUIView()
        {
            return UIView;
        }
    }
}
#endif
