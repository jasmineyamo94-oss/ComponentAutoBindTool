using System;
using UnityEngine;

namespace ComponentAutoBindTool.AssetCore
{
    public sealed class AssetHandle<T> : IAssetHandle where T : UnityEngine.Object
    {
        private Action m_Release;
        private T m_Asset;
        internal AssetHandle(string key, T asset, Action release)
        {
            Key = key;
            m_Asset = asset;
            m_Release = release;
        }
        public string Key { get; }
        public bool IsReleased { get; private set; }
        public T Asset => IsReleased ? null : m_Asset;
        public void Dispose()
        {
            if (IsReleased) return;
            IsReleased = true;
            m_Asset = null;
            Action release = m_Release;
            m_Release = null;
            release?.Invoke();
        }
    }

    public sealed class AssetInstanceHandle : IAssetHandle
    {
        private Action m_Release;
        private GameObject m_Instance;
        internal AssetInstanceHandle(string key, GameObject instance, Action release)
        {
            Key = key;
            m_Instance = instance;
            m_Release = release;
        }
        public string Key { get; }
        public bool IsReleased { get; private set; }
        public GameObject Instance => IsReleased ? null : m_Instance;
        public T GetRequiredComponent<T>() where T : Component
        {
            if (Instance != null && Instance.TryGetComponent(out T component)) return component;
            throw new MissingComponentException($"[{Key}] 实例已释放或缺少组件 {typeof(T).FullName}。");
        }
        public void Dispose()
        {
            if (IsReleased) return;
            IsReleased = true;
            m_Instance = null;
            Action release = m_Release;
            m_Release = null;
            release?.Invoke();
        }
    }
}
