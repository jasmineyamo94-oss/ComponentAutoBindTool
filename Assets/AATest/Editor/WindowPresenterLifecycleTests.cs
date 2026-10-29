using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ComponentAutoBindTool.ViewCore;
using NUnit.Framework;
using UnityEngine;
using VContainer;

namespace ComponentAutoBindTool.Tests
{
    public class WindowPresenterLifecycleTests
    {
        private readonly List<GameObject> m_Hosts = new();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject host in m_Hosts) UnityEngine.Object.DestroyImmediate(host);
            m_Hosts.Clear();
        }

        private sealed class CallbackDisposable : IDisposable
        {
            private readonly Action m_Callback;

            public CallbackDisposable(Action callback)
            {
                m_Callback = callback;
            }

            public void Dispose() => m_Callback();
        }

        private sealed class WindowArgs : IViewArgs
        {
            public int Value;
        }

        private sealed class Probe : WindowPresenterBase<object, WindowArgs>
        {
            public readonly List<string> Events = new();
            public readonly List<int> OpenedValues = new();
            public CancellationToken InstanceToken => DestroyToken;
            public CancellationToken SessionToken => OpenSessionToken;
            public Action<string> PhaseCallback { get; set; }
            public Action OpenCallback { get; set; }
            public int Resumes { get; private set; }
            public int Ticks { get; private set; }

            public Probe() : base(new object())
            {
            }

            public WidgetProbe Bind(WidgetViewBase host) => BindWidget<WidgetProbe>(host);
            public void Unbind(IWidgetPresenter widget) => UnbindWidget(widget);
            public void TrackSession(Action callback) => AddToOpenSession(new CallbackDisposable(callback));
            public void TrackInstance(Action callback) => AddToInstanceLifetime(new CallbackDisposable(callback));

            protected override void OnCreate()
            {
                AddToInstanceLifetime(new CallbackDisposable(() => Record("InstanceDisposed")));
            }

            protected override void OnOpen(WindowArgs args)
            {
                OpenedValues.Add(args.Value);
                AddToOpenSession(new CallbackDisposable(() => Record("SessionDisposed")));
                OpenCallback?.Invoke();
            }

            protected override void OnResume() => Resumes++;
            protected override void OnPause() => Record("Pause");
            protected override void OnClose() => Record("Close");
            protected override void OnDestroy() => Record("Destroy");
            protected override void OnWindowTick(float deltaTime) => Ticks++;

            private void Record(string phase)
            {
                Events.Add(phase);
                PhaseCallback?.Invoke(phase);
            }
        }

        public sealed class WidgetViewData
        {
            public string Name;
            public List<string> Events;
        }

        public sealed class WidgetProbe : WidgetPresenterBase<WidgetViewData>
        {
            public Action<string> PhaseCallback { get; set; }
            public CancellationToken InstanceToken => DestroyToken;
            public int Opens { get; private set; }
            public int Ticks { get; private set; }
            public WidgetProbe BindChild(WidgetViewBase host) => BindWidget<WidgetProbe>(host);

            protected override void OnCreate()
            {
                AddToInstanceLifetime(new CallbackDisposable(() => Record("InstanceDisposed")));
            }

            protected override void OnOpen()
            {
                Opens++;
                AddToOpenSession(new CallbackDisposable(() => Record("SessionDisposed")));
            }

            protected override void OnPause() => Record("Pause");
            protected override void OnClose() => Record("Close");
            protected override void OnDestroy() => Record("Destroy");
            protected override void OnWidgetTick(float deltaTime) => Ticks++;

            private void Record(string phase)
            {
                View.Events.Add(View.Name + "." + phase);
                PhaseCallback?.Invoke(phase);
            }
        }

        private WidgetLifecycleHost CreateHost(string name, List<string> events)
        {
            var gameObject = new GameObject(name);
            m_Hosts.Add(gameObject);
            var host = gameObject.AddComponent<WidgetLifecycleHost>();
            host.UIView = new WidgetViewData { Name = name, Events = events };
            return host;
        }

        private static IObjectResolver CreateScope()
        {
            var builder = new ContainerBuilder();
            builder.Register<WidgetProbe>(Lifetime.Transient);
            return builder.Build();
        }

        private static Probe CreateWindow(IObjectResolver scope = null, bool open = true)
        {
            var probe = new Probe();
            var lifecycle = (IWindowPresenter)probe;
            lifecycle.InternalAttach(null, scope);
            lifecycle.InternalOnCreate();
            if (open)
            {
                lifecycle.InternalOnOpen(new WindowArgs { Value = 1 });
                lifecycle.InternalOnResume();
            }
            return probe;
        }

        [Test]
        public void DestroyActiveWindowPausesClosesThenDestroysAndReleasesResourcesOnce()
        {
            var probe = new Probe();
            var lifecycle = (IWindowPresenter)probe;
            lifecycle.InternalOnCreate();
            lifecycle.InternalOnOpen(new WindowArgs { Value = 1 });
            lifecycle.InternalOnResume();
            CancellationToken sessionToken = probe.SessionToken;
            CancellationToken instanceToken = probe.InstanceToken;

            lifecycle.InternalOnDestroy();
            lifecycle.InternalOnDestroy();

            Assert.That(probe.Events, Is.EqualTo(new[]
            {
                "Pause", "Close", "SessionDisposed", "Destroy", "InstanceDisposed"
            }));
            Assert.That(sessionToken.IsCancellationRequested, Is.True);
            Assert.That(instanceToken.IsCancellationRequested, Is.True);
        }

        [Test]
        public void ClosePausesWholeWidgetTreeBeforeClosingAndRetainsInstanceResources()
        {
            using IObjectResolver scope = CreateScope();
            Probe probe = CreateWindow(scope);
            var lifecycle = (IWindowPresenter)probe;
            WidgetProbe left = probe.Bind(CreateHost("left", probe.Events));
            WidgetProbe nested = left.BindChild(CreateHost("nested", probe.Events));
            WidgetProbe right = probe.Bind(CreateHost("right", probe.Events));
            CancellationToken instanceToken = probe.InstanceToken;

            lifecycle.InternalOnClose();
            lifecycle.InternalOnClose();

            Assert.That(probe.Events, Is.EqualTo(new[]
            {
                "nested.Pause", "left.Pause", "right.Pause", "Pause",
                "right.Close", "right.SessionDisposed", "nested.Close", "nested.SessionDisposed",
                "left.Close", "left.SessionDisposed", "Close", "SessionDisposed"
            }));
            Assert.That(instanceToken.IsCancellationRequested, Is.False);
            Assert.That(left.InstanceToken.IsCancellationRequested || nested.InstanceToken.IsCancellationRequested ||
                right.InstanceToken.IsCancellationRequested, Is.False);
            lifecycle.InternalOnDestroy();
        }

        [Test]
        public void RepeatedPauseCloseAndDestroyDoNotRepeatCallbacks()
        {
            Probe probe = CreateWindow();
            var lifecycle = (IWindowPresenter)probe;
            lifecycle.InternalOnResume();
            lifecycle.InternalOnPause();
            lifecycle.InternalOnPause();
            lifecycle.InternalOnClose();
            lifecycle.InternalOnClose();
            lifecycle.InternalOnDestroy();
            lifecycle.InternalOnDestroy();

            Assert.That(probe.Resumes, Is.EqualTo(1));
            Assert.That(probe.Events, Is.EqualTo(new[]
            {
                "Pause", "Close", "SessionDisposed", "Destroy", "InstanceDisposed"
            }));
        }

        [Test]
        public void NeverOpenedWindowDoesNotInventPauseOrCloseAndDestroyedWindowCannotReopen()
        {
            Probe probe = CreateWindow(open: false);
            var lifecycle = (IWindowPresenter)probe;
            lifecycle.InternalOnResume();
            lifecycle.InternalOnPause();
            lifecycle.InternalOnClose();
            lifecycle.InternalOnDestroy();
            lifecycle.InternalOnOpen(new WindowArgs { Value = 2 });
            lifecycle.InternalOnResume();
            lifecycle.InternalTick(1f);

            Assert.That(probe.Events, Is.EqualTo(new[] { "Destroy", "InstanceDisposed" }));
            Assert.That(probe.OpenedValues, Is.Empty);
            Assert.That(probe.Resumes, Is.Zero);
            Assert.That(probe.Ticks, Is.Zero);
        }

        [Test]
        public void RepeatedOpenRefreshesArgumentsAndWidgetSessionWithoutPauseOrClose()
        {
            using IObjectResolver scope = CreateScope();
            Probe probe = CreateWindow(scope);
            var lifecycle = (IWindowPresenter)probe;
            WidgetProbe child = probe.Bind(CreateHost("child", probe.Events));
            CancellationToken oldSession = probe.SessionToken;

            lifecycle.InternalOnOpen(new WindowArgs { Value = 2 });
            lifecycle.InternalTick(1f);

            Assert.That(probe.OpenedValues, Is.EqualTo(new[] { 1, 2 }));
            Assert.That(probe.Events, Is.EqualTo(new[] { "SessionDisposed", "child.SessionDisposed" }));
            Assert.That(oldSession.IsCancellationRequested, Is.True);
            Assert.That(probe.SessionToken.IsCancellationRequested, Is.False);
            Assert.That(probe.Resumes, Is.EqualTo(1));
            Assert.That(probe.Ticks, Is.EqualTo(1));
            Assert.That(child.Opens, Is.EqualTo(2));
            Assert.That(child.Ticks, Is.EqualTo(1));
            lifecycle.InternalOnDestroy();
        }

        [Test]
        public void HiddenWidgetDoesNotRepeatPauseWhenWindowCloses()
        {
            using IObjectResolver scope = CreateScope();
            Probe probe = CreateWindow(scope);
            WidgetProbe child = probe.Bind(CreateHost("child", probe.Events));
            child.SetVisible(false);
            probe.Events.Clear();

            ((IWindowPresenter)probe).InternalOnClose();

            Assert.That(probe.Events, Is.EqualTo(new[]
            {
                "Pause", "child.Close", "child.SessionDisposed", "Close", "SessionDisposed"
            }));
            ((IWindowPresenter)probe).InternalOnDestroy();
        }

        [Test]
        public void CallbackFailuresStillPauseCloseDestroyAndReleaseEveryWidget()
        {
            using IObjectResolver scope = CreateScope();
            Probe probe = CreateWindow(scope);
            WidgetProbe left = probe.Bind(CreateHost("left", probe.Events));
            WidgetProbe right = probe.Bind(CreateHost("right", probe.Events));
            left.PhaseCallback = phase =>
            {
                if (phase == "Pause") throw new InvalidOperationException("left pause");
            };
            right.PhaseCallback = phase =>
            {
                if (phase == "Close" || phase == "Destroy") throw new InvalidOperationException("right " + phase);
            };
            probe.PhaseCallback = phase =>
            {
                if (phase == "Pause" || phase == "Close" || phase == "Destroy")
                    throw new InvalidOperationException("window " + phase);
            };
            CancellationToken instanceToken = probe.InstanceToken;
            CancellationToken leftToken = left.InstanceToken;
            CancellationToken rightToken = right.InstanceToken;

            AggregateException failure = Assert.Throws<AggregateException>(() => ((IWindowPresenter)probe).InternalOnDestroy());

            Assert.That(failure.Flatten().InnerExceptions.Select(exception => exception.Message), Is.EquivalentTo(new[]
            {
                "left pause", "window Pause", "right Close", "window Close", "right Destroy", "window Destroy"
            }));
            Assert.That(probe.Events, Is.EqualTo(new[]
            {
                "left.Pause", "right.Pause", "Pause", "right.Close", "right.SessionDisposed",
                "left.Close", "left.SessionDisposed", "Close", "SessionDisposed",
                "right.Destroy", "right.InstanceDisposed", "left.Destroy", "left.InstanceDisposed", "Destroy", "InstanceDisposed"
            }));
            Assert.That(instanceToken.IsCancellationRequested && leftToken.IsCancellationRequested && rightToken.IsCancellationRequested, Is.True);
            Assert.DoesNotThrow(() => ((IWindowPresenter)probe).InternalOnDestroy());
        }

        [Test]
        public void SingleCloseFailurePropagatesOriginalExceptionAfterSessionCleanup()
        {
            Probe probe = CreateWindow();
            var expectedFailure = new InvalidOperationException("window close");
            probe.PhaseCallback = phase =>
            {
                if (phase == "Close") throw expectedFailure;
            };

            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => ((IWindowPresenter)probe).InternalOnClose());

            Assert.That(failure, Is.SameAs(expectedFailure));
            Assert.That(probe.Events, Is.EqualTo(new[] { "Pause", "Close", "SessionDisposed" }));
            Assert.DoesNotThrow(() => ((IWindowPresenter)probe).InternalOnDestroy());
        }

        [Test]
        public void CancellationAndDisposeFailuresDoNotSkipOtherResources()
        {
            Probe probe = CreateWindow();
            probe.SessionToken.Register(() => throw new InvalidOperationException("session cancel"));
            probe.InstanceToken.Register(() => throw new InvalidOperationException("instance cancel"));
            int releases = 0;
            probe.TrackSession(() => releases++);
            probe.TrackSession(() => throw new InvalidOperationException("session dispose"));
            probe.TrackInstance(() => releases++);
            probe.TrackInstance(() => throw new InvalidOperationException("instance dispose"));

            AggregateException failure = Assert.Throws<AggregateException>(() => ((IWindowPresenter)probe).InternalOnDestroy());

            Assert.That(failure.Flatten().InnerExceptions.Select(exception => exception.Message), Is.EquivalentTo(new[]
            {
                "session cancel", "session dispose", "instance cancel", "instance dispose"
            }));
            Assert.That(releases, Is.EqualTo(2));
            Assert.That(probe.Events, Is.EqualTo(new[] { "Pause", "Close", "SessionDisposed", "Destroy", "InstanceDisposed" }));
            Assert.DoesNotThrow(() => ((IWindowPresenter)probe).InternalOnDestroy());
        }

        [Test]
        public void ReentrantCloseAndDestroyWaitForCurrentPhaseAndRunOnce()
        {
            using IObjectResolver scope = CreateScope();
            Probe probe = CreateWindow(scope);
            var lifecycle = (IWindowPresenter)probe;
            WidgetProbe child = probe.Bind(CreateHost("child", probe.Events));
            child.PhaseCallback = phase =>
            {
                if (phase != "Pause") return;
                lifecycle.InternalOnClose();
                lifecycle.InternalOnDestroy();
                probe.Events.Add("child.PauseCompleted");
            };
            probe.PhaseCallback = phase =>
            {
                lifecycle.InternalOnClose();
                lifecycle.InternalOnDestroy();
            };

            lifecycle.InternalOnClose();
            lifecycle.InternalOnDestroy();

            Assert.That(probe.Events, Is.EqualTo(new[]
            {
                "child.Pause", "child.PauseCompleted", "Pause", "child.Close", "child.SessionDisposed", "Close", "SessionDisposed",
                "child.Destroy", "child.InstanceDisposed", "Destroy", "InstanceDisposed"
            }));
        }

        [Test]
        public void DestroyRequestedDuringPauseWaitsUntilPauseCompletes()
        {
            Probe probe = CreateWindow();
            var lifecycle = (IWindowPresenter)probe;
            probe.PhaseCallback = phase =>
            {
                if (phase != "Pause") return;
                lifecycle.InternalOnDestroy();
                probe.Events.Add("PauseCompleted");
            };

            lifecycle.InternalOnPause();

            Assert.That(probe.Events, Is.EqualTo(new[]
            {
                "Pause", "PauseCompleted", "Close", "SessionDisposed", "Destroy", "InstanceDisposed"
            }));
        }

        [Test]
        public void WidgetUnbindingItselfDuringPauseDoesNotSkipRemainingSibling()
        {
            using IObjectResolver scope = CreateScope();
            Probe probe = CreateWindow(scope);
            WidgetProbe left = probe.Bind(CreateHost("left", probe.Events));
            probe.Bind(CreateHost("right", probe.Events));
            left.PhaseCallback = phase =>
            {
                if (phase == "Pause") probe.Unbind(left);
            };

            ((IWindowPresenter)probe).InternalOnClose();

            Assert.That(probe.Events, Does.Contain("right.Pause"));
            Assert.That(probe.Events.IndexOf("right.Pause"), Is.LessThan(probe.Events.IndexOf("Pause")));
            Assert.That(probe.Events.Count(phase => phase == "right.Close"), Is.EqualTo(1));
            ((IWindowPresenter)probe).InternalOnDestroy();
        }

        [Test]
        public void DestroyDuringOnOpenDoesNotReopenChildWidgets()
        {
            using IObjectResolver scope = CreateScope();
            Probe probe = CreateWindow(scope, false);
            var lifecycle = (IWindowPresenter)probe;
            WidgetProbe child = probe.Bind(CreateHost("child", probe.Events));
            probe.OpenCallback = lifecycle.InternalOnDestroy;

            lifecycle.InternalOnOpen(new WindowArgs { Value = 1 });
            lifecycle.InternalOnResume();
            lifecycle.InternalTick(1f);

            Assert.That(child.Opens, Is.Zero);
            Assert.That(probe.Resumes, Is.Zero);
            Assert.That(probe.Ticks, Is.Zero);
            Assert.That(probe.Events, Is.EqualTo(new[]
            {
                "Close", "SessionDisposed", "child.Destroy", "child.InstanceDisposed", "Destroy", "InstanceDisposed"
            }));
        }

        [Test]
        public void DestroyDuringPreviousSessionCleanupPreventsRefreshFromReopeningWindow()
        {
            Probe probe = CreateWindow();
            var lifecycle = (IWindowPresenter)probe;
            probe.TrackSession(lifecycle.InternalOnDestroy);

            lifecycle.InternalOnOpen(new WindowArgs { Value = 2 });
            lifecycle.InternalOnResume();
            lifecycle.InternalTick(1f);

            Assert.That(probe.OpenedValues, Is.EqualTo(new[] { 1 }));
            Assert.That(probe.Events, Is.EqualTo(new[] { "Pause", "Close", "SessionDisposed", "Destroy", "InstanceDisposed" }));
            Assert.That(probe.Ticks, Is.Zero);
            Assert.That(probe.SessionToken.CanBeCanceled, Is.False);
        }
    }
}
