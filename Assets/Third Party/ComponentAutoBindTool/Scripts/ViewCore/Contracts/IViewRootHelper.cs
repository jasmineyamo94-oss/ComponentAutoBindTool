using UnityEngine;

// ReSharper disable once CheckNamespace
namespace ComponentAutoBindTool.ViewCore
{
    public interface IViewRootHelper
    {
        Transform GetViewRoot(string viewName, UILayer layer = UILayer.Normal);
    }
}