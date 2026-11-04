using System;
using UnityEngine;
using UnityEngine.Events;

namespace PrefabAutoBind
{
    public abstract class DirectReferenceViewBase : MonoBehaviour
    {
        private bool uiEventsRegistered;

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

        protected virtual void OnEnable()
        {
            RegisterUIEvents();
        }

        protected virtual void OnDisable()
        {
            UnregisterUIEvents();
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
                    RemoveUIEvents();
                }
                catch (Exception cleanupException)
                {
                    Debug.LogException(cleanupException, this);
                }

                uiEventsRegistered = false;
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
