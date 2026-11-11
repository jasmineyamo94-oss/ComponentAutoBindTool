using UnityEngine;

namespace AAGame.Assets
{
    public enum AssetBackendKind { Resources, Addressables, YooAsset }

    [CreateAssetMenu(menuName = "项目工具/资源后端配置")]
    public sealed class AssetBackendSettings : ScriptableObject
    {
        [SerializeField] private AssetBackendKind _backend;
        [SerializeField] private string _resourcesPrefix = "AAGameDemo";
        [SerializeField] private string _yooPackageName = "AAGameDemo";
        public AssetBackendKind Backend { get => _backend; set => _backend = value; }
        public string ResourcesPrefix => _resourcesPrefix;
        public string YooPackageName => _yooPackageName;
    }
}
