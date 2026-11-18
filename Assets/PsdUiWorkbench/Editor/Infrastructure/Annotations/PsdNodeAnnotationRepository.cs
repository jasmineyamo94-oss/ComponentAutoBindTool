using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PsdUiWorkbench.Core;
using UnityEditor;
using UnityEngine;

namespace PsdUiWorkbench.Infrastructure
{
    public sealed class PsdNodeAnnotationRepository : IPsdNodeAnnotationRepository, IDisposable
    {
        public const string ConfigFolder = "Assets/PsdUiWorkbench/Configs/Annotations";
        private readonly Dictionary<PsdNodeAnnotationConfig, PsdNodeAnnotationState> _observed =
            new Dictionary<PsdNodeAnnotationConfig, PsdNodeAnnotationState>();
        private readonly Action<PsdNodeAnnotationConfig> _saveAsset;
        private bool _disposed;
        public event Action Changed;

        public PsdNodeAnnotationRepository() : this(config => AssetDatabase.SaveAssetIfDirty(config)) { }

        // 仅替换真实存储边界，便于覆盖写盘前后失败的回滚行为。
        internal PsdNodeAnnotationRepository(Action<PsdNodeAnnotationConfig> saveAsset)
        {
            _saveAsset = saveAsset ?? throw new ArgumentNullException(nameof(saveAsset));
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        private static string NormalizeGuid(string assetGuid)
        {
            if (!Guid.TryParseExact(assetGuid, "N", out var guid))
                throw new ArgumentException("PSD GUID 必须是 32 位十六进制字符串。", nameof(assetGuid));
            return guid.ToString("N");
        }

        public static string GetConfigPath(string assetGuid) => ConfigFolder + "/" + NormalizeGuid(assetGuid) + ".asset";

        private PsdNodeAnnotationConfig Find(string assetGuid)
        {
            string path = GetConfigPath(assetGuid);
            var config = AssetDatabase.LoadAssetAtPath<PsdNodeAnnotationConfig>(path);
            if (config == null)
            {
                if (Occupied(path)) throw new IOException("批注配置路径已被其他资源占用：" + path);
                return null;
            }
            if (!string.Equals(config.AssetGuid, assetGuid, StringComparison.Ordinal))
                throw new InvalidOperationException("批注配置的 PSD GUID 与文件名不一致。");
            if (!_observed.ContainsKey(config)) _observed.Add(config, config.Read());
            return config;
        }

        public PsdNodeAnnotationState Load(string assetGuid)
        {
            ThrowIfDisposed();
            return Find(NormalizeGuid(assetGuid))?.Read() ?? new PsdNodeAnnotationState();
        }

        public void Save(string assetGuid, PsdNodeAnnotationState state)
        {
            ThrowIfDisposed();
            if (state == null) throw new ArgumentNullException(nameof(state));
            assetGuid = NormalizeGuid(assetGuid);
            string path = GetConfigPath(assetGuid);
            var config = Find(assetGuid);
            var before = config != null ? config.Read() : new PsdNodeAnnotationState();
            if (before.Equals(state)) return;
            bool created = false;
            if (config == null)
            {
                EnsureFolder(ConfigFolder);
                // 创建前再次检查，绝不覆盖其他资源、未导入文件或孤立元文件。
                if (Occupied(path)) throw new IOException("批注配置路径已被占用：" + path);
                config = ScriptableObject.CreateInstance<PsdNodeAnnotationConfig>();
                config.Write(assetGuid, before);
                try
                {
                    AssetDatabase.CreateAsset(config, path);
                    if (AssetDatabase.LoadAssetAtPath<PsdNodeAnnotationConfig>(path) != config)
                        throw new IOException("无法创建批注配置：" + path);
                    created = true;
                }
                catch
                {
                    if (AssetDatabase.LoadAssetAtPath<PsdNodeAnnotationConfig>(path) == config) AssetDatabase.DeleteAsset(path);
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
                if (created && AssetDatabase.LoadAssetAtPath<PsdNodeAnnotationConfig>(path) == config)
                {
                    _observed.Remove(config);
                    AssetDatabase.DeleteAsset(path);
                }
                throw;
            }

            // 先完整落盘，成功后才登记一次 Undo；失败不改动其他工具的撤销记录。
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            const string undoName = "PSD 工作台节点批注";
            try
            {
                config.Write(assetGuid, before);
                Undo.SetCurrentGroupName(undoName);
                Undo.RegisterCompleteObjectUndo(config, undoName);
                config.Write(assetGuid, state);
                Undo.CollapseUndoOperations(group);
                EditorUtility.ClearDirty(config);
                _observed[config] = state;
            }
            finally { Undo.IncrementCurrentGroup(); }
        }

        private void Persist(PsdNodeAnnotationConfig config, string assetGuid,
            PsdNodeAnnotationState before, PsdNodeAnnotationState after)
        {
            string path = AssetDatabase.GetAssetPath(config);
            bool dirty = EditorUtility.IsDirty(config);
            byte[] disk = File.ReadAllBytes(path);
            try
            {
                if (new FileInfo(path).IsReadOnly || !AssetDatabase.IsOpenForEdit(path, out string reason, StatusQueryOptions.ForceUpdate))
                    throw new IOException("批注配置不可写：" + path);
                config.Write(assetGuid, after);
                EditorUtility.SetDirty(config);
                _saveAsset(config);
                if (EditorUtility.IsDirty(config)) throw new IOException("批注配置未成功保存：" + path);
            }
            catch
            {
                config.Write(assetGuid, before);
                if (dirty) EditorUtility.SetDirty(config);
                else EditorUtility.ClearDirty(config);
                // 即使保存器写入后才抛错，也只还原目标配置，不触碰任何其他资源。
                if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(disk)) File.WriteAllBytes(path, disk);
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
            // 无关 Undo 不写盘、不通知；批注变化保存成功后再让会话恢复快照。
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
            if (Occupied(path)) throw new IOException("批注配置目录被其他文件或未导入目录占用：" + path);
            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent)) throw new IOException("无法创建批注配置目录：" + path);
            EnsureFolder(parent);
            if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, Path.GetFileName(path))))
                throw new IOException("无法创建批注配置目录：" + path);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PsdNodeAnnotationRepository));
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
