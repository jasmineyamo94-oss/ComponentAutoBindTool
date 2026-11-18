using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PsdUiWorkbench.Core;
using UnityEditor;
using UnityEngine;

namespace PsdUiWorkbench.Infrastructure
{
    public sealed class PsdNodeNameRepository : IPsdNodeNameRepository, IDisposable
    {
        public const string ConfigFolder = "Assets/PsdUiWorkbench/Configs/Naming";
        private readonly Dictionary<PsdNodeNameConfig, PsdNodeNameState> _observed = new Dictionary<PsdNodeNameConfig, PsdNodeNameState>();
        private readonly Action<PsdNodeNameConfig> _saveAsset;
        private bool _disposed;
        public event Action Changed;

        public PsdNodeNameRepository() : this(config => AssetDatabase.SaveAssetIfDirty(config)) { }

        // 仅存储边界可替换，测试真实资产事务时可注入确定的磁盘故障。
        internal PsdNodeNameRepository(Action<PsdNodeNameConfig> saveAsset)
        {
            _saveAsset = saveAsset ?? throw new ArgumentNullException(nameof(saveAsset));
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        public static string GetConfigPath(string assetGuid)
        {
            if (!Guid.TryParseExact(assetGuid, "N", out _)) throw new ArgumentException("PSD GUID 必须是 32 位十六进制字符串。", nameof(assetGuid));
            return ConfigFolder + "/" + assetGuid + ".asset";
        }

        private PsdNodeNameConfig Find(string assetGuid)
        {
            string path = GetConfigPath(assetGuid);
            var config = AssetDatabase.LoadAssetAtPath<PsdNodeNameConfig>(path);
            if (config == null)
            {
                if (Occupied(path)) throw new IOException("命名配置路径已被其他资源占用：" + path);
                return null;
            }
            if (!string.Equals(config.AssetGuid, assetGuid, StringComparison.Ordinal))
                throw new InvalidOperationException("命名配置的 PSD GUID 与文件名不一致。");
            if (!_observed.ContainsKey(config)) _observed.Add(config, config.Read());
            return config;
        }

        public PsdNodeNameState Load(string assetGuid)
        {
            ThrowIfDisposed();
            return Find(assetGuid)?.Read() ?? new PsdNodeNameState();
        }

        public void Save(string assetGuid, PsdNodeNameState state)
        {
            ThrowIfDisposed();
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (!PsdNodeNaming.IsValidRoot(state.RootName) ||
                state.Overrides.Values.Any(name => !PsdNodeNaming.IsValid(name)))
                throw new ArgumentException("命名配置包含无效名称。", nameof(state));
            string path = GetConfigPath(assetGuid);
            var config = Find(assetGuid);
            var before = config != null ? config.Read() : new PsdNodeNameState();
            if (before.Equals(state)) return;
            bool created = false;
            if (config == null)
            {
                EnsureFolder(ConfigFolder);
                // 创建前再次检查，绝不让 CreateAsset 覆盖已有资源或磁盘文件。
                if (Occupied(path)) throw new IOException("命名配置路径已被占用：" + path);
                config = ScriptableObject.CreateInstance<PsdNodeNameConfig>();
                config.Write(assetGuid, before);
                try
                {
                    AssetDatabase.CreateAsset(config, path);
                    if (AssetDatabase.LoadAssetAtPath<PsdNodeNameConfig>(path) != config)
                        throw new IOException("无法创建命名配置：" + path);
                    created = true;
                }
                catch
                {
                    if (AssetDatabase.LoadAssetAtPath<PsdNodeNameConfig>(path) == config) AssetDatabase.DeleteAsset(path);
                    if (config != null && !EditorUtility.IsPersistent(config)) UnityEngine.Object.DestroyImmediate(config);
                    throw;
                }
            }
            try
            {
                Persist(config, assetGuid, before, state);
            }
            catch
            {
                if (created && AssetDatabase.LoadAssetAtPath<PsdNodeNameConfig>(path) == config)
                {
                    _observed.Remove(config);
                    AssetDatabase.DeleteAsset(path);
                }
                throw;
            }

            // 先落盘，再登记本次唯一的 Undo。失败路径完全不接触 Undo 栈，保留其他工具的撤销记录。
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            const string undoName = "PSD 工作台节点命名";
            try
            {
                config.Write(assetGuid, before);
                Undo.SetCurrentGroupName(undoName);
                Undo.RegisterCompleteObjectUndo(config, undoName);
                config.Write(assetGuid, state);
                Undo.CollapseUndoOperations(group);
                EditorUtility.ClearDirty(config); // 当前内存与刚保存的目标资产一致。
                _observed[config] = state;
            }
            finally { Undo.IncrementCurrentGroup(); }
        }

        private void Persist(PsdNodeNameConfig config, string assetGuid, PsdNodeNameState before, PsdNodeNameState after)
        {
            string path = AssetDatabase.GetAssetPath(config);
            bool dirty = EditorUtility.IsDirty(config);
            byte[] disk = File.ReadAllBytes(path);
            try
            {
                if (new FileInfo(path).IsReadOnly || !AssetDatabase.IsOpenForEdit(path, out string reason, StatusQueryOptions.ForceUpdate))
                    throw new IOException("命名配置不可写：" + path);
                config.Write(assetGuid, after);
                EditorUtility.SetDirty(config);
                _saveAsset(config);
                if (EditorUtility.IsDirty(config)) throw new IOException("命名配置未成功保存：" + path);
            }
            catch
            {
                config.Write(assetGuid, before);
                if (dirty) EditorUtility.SetDirty(config);
                else EditorUtility.ClearDirty(config);
                // 目标保存若中途写入后抛错，尽力恢复该文件；从不保存或回滚其他资产。
                if (File.Exists(path) && !File.ReadAllBytes(path).SequenceEqual(disk)) File.WriteAllBytes(path, disk);
                throw;
            }
        }

        public void Flush()
        {
            ThrowIfDisposed();
            foreach (var pair in _observed.ToArray())
            {
                if (pair.Key == null || !EditorUtility.IsDirty(pair.Key)) continue;
                var state = pair.Key.Read();
                Persist(pair.Key, pair.Key.AssetGuid, pair.Value, state);
                _observed[pair.Key] = state;
            }
        }

        private void OnUndoRedo()
        {
            bool changed = _observed.Any(pair => pair.Key != null && !pair.Value.Equals(pair.Key.Read()));
            if (!changed) return;
            // 只保存此仓库打开过且真实变化的命名资产，不因别的仓库 Undo 产生通知。
            foreach (var pair in _observed.ToArray())
                if (pair.Key != null && !pair.Value.Equals(pair.Key.Read())) EditorUtility.SetDirty(pair.Key);
            Flush();
            Changed?.Invoke();
        }

        private static bool Occupied(string path)
            => File.Exists(path) || Directory.Exists(path) || File.Exists(path + ".meta") ||
               !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path));

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            if (Occupied(path)) throw new IOException("命名配置目录被其他文件或未导入目录占用：" + path);
            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent)) throw new IOException("无法创建命名配置目录：" + path);
            EnsureFolder(parent);
            if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, Path.GetFileName(path))))
                throw new IOException("无法创建命名配置目录：" + path);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PsdNodeNameRepository));
        }

        public void Dispose()
        {
            if (_disposed) return;
            Undo.undoRedoPerformed -= OnUndoRedo;
            try { Flush(); }
            finally { _disposed = true; }
        }
    }
}
