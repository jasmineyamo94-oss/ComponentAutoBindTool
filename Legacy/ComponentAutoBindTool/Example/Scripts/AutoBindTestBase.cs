using UnityEngine;
using VContainer;
using VContainer.Unity;
using Third_Party.ComponentAutoBindTool.Scripts.ViewCore;

namespace Third_Party.ComponentAutoBindTool.Example.Scripts
{
    public class AutoBindTestBase : LifetimeScope, IAutoBindHost
    {
        protected override void Configure(IContainerBuilder builder)
        {
            EnsureAutoBind(gameObject);
        }

        public virtual void EnsureAutoBind(GameObject go){ }
    }
}
