using System;
using UnityEngine;

namespace ComponentAutoBindTool.AssetCore
{
    public sealed class AssetHandle<T> : IAssetHandle where T : UnityEngine.Object
    {
        private Action _release;
        private T _asset;
        internal AssetHandle(string key, T asset, Action release)
        {
            Key = key;
            _asset = asset;
            _release = release;
        }
        public string Key { get; }
        public bool IsReleased { get; private set; }
        public T Asset => IsReleased ? null : _asset;
        public void Dispose()
        {
            if (IsReleased) return;
            IsReleased = true;
            _asset = null;
            Action release = _release;
            _release = null;
            release?.Invoke();
        }
    }

    public sealed class AssetInstanceHandle : IAssetHandle
    {
        private Action _release;
        private GameObject _instance;
        internal AssetInstanceHandle(string key, GameObject instance, Action release)
        {
            Key = key;
            _instance = instance;
            _release = release;
        }
        public string Key { get; }
        public bool IsReleased { get; private set; }
        public GameObject Instance => IsReleased ? null : _instance;
        public T GetRequiredComponent<T>() where T : Component
        {
            if (Instance != null && Instance.TryGetComponent(out T component)) return component;
            throw new MissingComponentException($"[{Key}] 实例已释放或缺少组件 {typeof(T).FullName}。");
        }
        public void Dispose()
        {
            if (IsReleased) return;
            IsReleased = true;
            _instance = null;
            Action release = _release;
            _release = null;
            release?.Invoke();
        }
    }
}
