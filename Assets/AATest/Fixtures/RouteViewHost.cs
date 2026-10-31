using System;
using System.Collections.Generic;
using ComponentAutoBindTool.ViewCore;
using UnityEngine;
using VContainer;

namespace ComponentAutoBindTool.Tests
{
    public class RouteArgs : IViewArgs { public int Value; }
    public sealed class OtherRouteArgs : IViewArgs { }
    public sealed class DerivedRouteArgs : RouteArgs { }
    public enum RoutePresenterKind { Typed, Other, MissingContract, Empty }

    public class RouteViewHost : ViewBase
    {
        public RoutePresenterKind PresenterKind;
        public override void EnsureAutoBind(GameObject go) { }
        public override void ConfigureWindow(IContainerBuilder builder)
        {
            builder.Register<RouteScopeProbe>(Lifetime.Scoped);
            switch (PresenterKind)
            {
                case RoutePresenterKind.Typed: RegisterWindow<RoutePresenter, object>(builder, new object()); break;
                case RoutePresenterKind.Other: RegisterWindow<OtherRoutePresenter, object>(builder, new object()); break;
                case RoutePresenterKind.Empty: RegisterWindow<EmptyRoutePresenter, object>(builder, new object()); break;
                default: builder.Register<UncontractedRoutePresenter>(Lifetime.Scoped).As<IWindowPresenter>(); break;
            }
        }
    }

    public sealed class RouteProbe
    {
        public int Creates, Opens, Closes, Destroys, ScopeDisposals, SessionDisposals, LastValue;
        public IViewArgs LastArgs;
        public readonly Dictionary<int, IWindowContext> Contexts = new();
    }

    public sealed class RouteScopeProbe : IDisposable
    {
        private readonly RouteProbe m_Probe;
        public RouteScopeProbe(RouteProbe probe) { m_Probe = probe; }
        public void Dispose() => m_Probe.ScopeDisposals++;
    }

    public sealed class RoutePresenter : WindowPresenterBase<object, RouteArgs>
    {
        private readonly RouteProbe m_Probe;
        public RoutePresenter(object view, RouteProbe probe, RouteScopeProbe lifetime) : base(view) { m_Probe = probe; }
        protected override void OnCreate() { m_Probe.Creates++; m_Probe.Contexts[Context.InstanceId] = Context; }
        protected override void OnOpen(RouteArgs args)
        {
            m_Probe.Opens++;
            m_Probe.LastArgs = args;
            m_Probe.LastValue = args.Value;
            AddToOpenSession(new SessionCleanup(m_Probe));
        }
        protected override void OnClose() => m_Probe.Closes++;
        protected override void OnDestroy() => m_Probe.Destroys++;
        private sealed class SessionCleanup : IDisposable
        {
            private readonly RouteProbe m_Probe;
            public SessionCleanup(RouteProbe probe) { m_Probe = probe; }
            public void Dispose() => m_Probe.SessionDisposals++;
        }
    }

    public sealed class OtherRoutePresenter : WindowPresenterBase<object, OtherRouteArgs>
    {
        public OtherRoutePresenter(object view, RouteScopeProbe lifetime) : base(view) { }
        protected override void OnOpen(OtherRouteArgs args) { }
    }

    public sealed class EmptyRoutePresenter : WindowPresenterBase<object, EmptyViewArgs>
    {
        private readonly RouteProbe m_Probe;
        public EmptyRoutePresenter(object view, RouteProbe probe) : base(view) { m_Probe = probe; }
        protected override void OnOpen(EmptyViewArgs args) { m_Probe.Opens++; m_Probe.LastArgs = args; }
    }

    public sealed class UncontractedRoutePresenter : IWindowPresenter
    {
        private readonly RouteProbe m_Probe;
        public UncontractedRoutePresenter(RouteProbe probe, RouteScopeProbe lifetime) { m_Probe = probe; }
        public void InternalAttach(IWindowContext context, IObjectResolver scope) { }
        public void InternalOnCreate() => m_Probe.Creates++;
        public void InternalOnOpen(IViewArgs args) => m_Probe.Opens++;
        public void InternalOnResume() { }
        public void InternalOnPause() { }
        public void InternalOnClose() { }
        public void InternalOnDestroy() => m_Probe.Destroys++;
        public void InternalTick(float deltaTime) { }
    }
}
