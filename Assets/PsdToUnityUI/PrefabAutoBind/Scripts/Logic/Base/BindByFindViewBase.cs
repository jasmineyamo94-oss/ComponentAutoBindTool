using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace PrefabAutoBind
{
    public abstract class BindByFindViewBase
    {
        private readonly List<BindByFindViewBase> childViews = new List<BindByFindViewBase>();
        private bool uiEventsRegistered;

        protected GameObject Root { get; private set; }

        public void Bind(GameObject root)
        {
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root));
            }

            Unbind();
            Root = root;

            try
            {
                BindUI(root);
                RegisterUIEvents();
            }
            catch
            {
                try
                {
                    Unbind();
                }
                catch (Exception cleanupException)
                {
                    Debug.LogException(cleanupException);
                }

                throw;
            }
        }

        public void Unbind()
        {
            Exception firstException = null;

            try
            {
                UnregisterUIEvents();
            }
            catch (Exception exception)
            {
                firstException = exception;
            }

            for (var index = childViews.Count - 1; index >= 0; index--)
            {
                try
                {
                    childViews[index].Unbind();
                }
                catch (Exception exception)
                {
                    if (firstException == null)
                    {
                        firstException = exception;
                    }
                    else
                    {
                        Debug.LogException(exception);
                    }
                }
            }

            childViews.Clear();
            Root = null;

            if (firstException != null)
            {
                throw new InvalidOperationException("Failed to completely unbind the UI view.", firstException);
            }
        }

        protected abstract void BindUI(GameObject root);

        protected abstract void AddUIEvents();

        protected abstract void RemoveUIEvents();

        protected static void AddUIEvent(UnityEvent uiEvent, UnityAction listener)
        {
            if (uiEvent == null)
            {
                throw new ArgumentNullException(nameof(uiEvent));
            }

            if (listener == null)
            {
                throw new ArgumentNullException(nameof(listener));
            }

            uiEvent.AddListener(listener);
        }

        protected static void AddUIEvent<T>(UnityEvent<T> uiEvent, UnityAction<T> listener)
        {
            if (uiEvent == null)
            {
                throw new ArgumentNullException(nameof(uiEvent));
            }

            if (listener == null)
            {
                throw new ArgumentNullException(nameof(listener));
            }

            uiEvent.AddListener(listener);
        }

        protected static void RemoveUIEvent(UnityEvent uiEvent, UnityAction listener)
        {
            if (uiEvent == null)
            {
                throw new ArgumentNullException(nameof(uiEvent));
            }

            if (listener == null)
            {
                throw new ArgumentNullException(nameof(listener));
            }

            uiEvent.RemoveListener(listener);
        }

        protected static void RemoveUIEvent<T>(UnityEvent<T> uiEvent, UnityAction<T> listener)
        {
            if (uiEvent == null)
            {
                throw new ArgumentNullException(nameof(uiEvent));
            }

            if (listener == null)
            {
                throw new ArgumentNullException(nameof(listener));
            }

            uiEvent.RemoveListener(listener);
        }

        protected T FindUI<T>(Transform parent, string path) where T : Component
        {
            if (parent == null)
            {
                throw new ArgumentNullException(nameof(parent));
            }

            var normalizedPath = path ?? string.Empty;
            var target = string.IsNullOrEmpty(normalizedPath)
                ? parent
                : parent.Find(normalizedPath);
            if (target == null)
            {
                throw new InvalidOperationException(
                    "Unable to find UI path '" + normalizedPath + "' below '" + parent.name +
                    "' for component '" + typeof(T).FullName + "'.");
            }

            var component = target.GetComponent<T>();
            if (component == null)
            {
                throw new MissingComponentException(
                    "UI path '" + normalizedPath + "' below '" + parent.name +
                    "' does not contain component '" + typeof(T).FullName + "'.");
            }

            return component;
        }

        protected T TrackChildView<T>(T childView) where T : BindByFindViewBase
        {
            if (childView == null)
            {
                throw new ArgumentNullException(nameof(childView));
            }

            if (!childViews.Contains(childView))
            {
                childViews.Add(childView);
            }

            return childView;
        }

        private void RegisterUIEvents()
        {
            if (uiEventsRegistered)
            {
                return;
            }

            uiEventsRegistered = true;
            try
            {
                AddUIEvents();
            }
            catch
            {
                try
                {
                    UnregisterUIEvents();
                }
                catch (Exception cleanupException)
                {
                    Debug.LogException(cleanupException);
                }

                throw;
            }
        }

        private void UnregisterUIEvents()
        {
            if (!uiEventsRegistered)
            {
                return;
            }

            try
            {
                RemoveUIEvents();
            }
            finally
            {
                uiEventsRegistered = false;
            }
        }
    }
}
