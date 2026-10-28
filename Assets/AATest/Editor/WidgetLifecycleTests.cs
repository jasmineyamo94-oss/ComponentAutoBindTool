using ComponentAutoBindTool.ViewCore;
using NUnit.Framework;

namespace ComponentAutoBindTool.Tests
{
    public class WidgetLifecycleTests
    {
        private sealed class Probe : WidgetPresenterBase<object>
        {
            public int Resumes, Pauses, Ticks;
            protected override void OnResume() => Resumes++;
            protected override void OnPause() => Pauses++;
            protected override void OnWidgetTick(float dt) => Ticks++;
        }

        [Test]
        public void LocalHideShowResumesTickExactlyOnce()
        {
            var probe = new Probe();
            var lifecycle = (IWidgetPresenter)probe;
            lifecycle.InternalOnOpen();
            lifecycle.InternalOnResume();
            probe.SetVisible(false);
            lifecycle.InternalTick(1);
            probe.SetVisible(true);
            lifecycle.InternalTick(1);
            lifecycle.InternalOnResume();
            Assert.That(probe.Ticks, Is.EqualTo(1));
            Assert.That(probe.Resumes, Is.EqualTo(2));
            Assert.That(probe.Pauses, Is.EqualTo(1));
            lifecycle.InternalOnDestroy();
        }

        [Test]
        public void LocalVisibilityCannotResumePausedParent()
        {
            var probe = new Probe();
            var lifecycle = (IWidgetPresenter)probe;
            lifecycle.InternalOnOpen();
            lifecycle.InternalOnResume();
            probe.SetVisible(false);
            lifecycle.InternalOnPause();
            probe.SetVisible(true);
            lifecycle.InternalTick(1);
            Assert.That(probe.Ticks, Is.Zero);
            Assert.That(probe.Pauses, Is.EqualTo(1));
            lifecycle.InternalOnResume();
            lifecycle.InternalTick(1);
            Assert.That(probe.Resumes, Is.EqualTo(2));
            Assert.That(probe.Ticks, Is.EqualTo(1));
            lifecycle.InternalOnClose();
            lifecycle.InternalOnDestroy();
        }
    }
}
