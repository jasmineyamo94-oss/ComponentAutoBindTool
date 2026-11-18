using System;
using System.Collections.Generic;
using System.Linq;
using PsdUiWorkbench.Core;
using UnityEditor;
using UnityEngine;

namespace PsdUiWorkbench.Infrastructure
{
    public sealed class PsdVisibilityRepository : IPsdVisibilityRepository, IPsdExportRepository, IDisposable
    {
        public const string ConfigFolder = "Assets/PsdUiWorkbench/Configs";
        private readonly HashSet<PsdVisibilityConfig> _pending = new HashSet<PsdVisibilityConfig>();
        private readonly HashSet<PsdVisibilityConfig> _loaded = new HashSet<PsdVisibilityConfig>();
        private bool _queued;
        private double _saveAfter;
        public event Action Changed;

        public PsdVisibilityRepository()
        {
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        public static string GetConfigPath(string assetGuid) => ConfigFolder + "/" + assetGuid + ".asset";

        private PsdVisibilityConfig Find(string assetGuid)
        {
            if (string.IsNullOrEmpty(assetGuid) || assetGuid.Length != 32) return null;
            var config = AssetDatabase.LoadAssetAtPath<PsdVisibilityConfig>(GetConfigPath(assetGuid));
            if (config != null)
            {
                if (config.AssetGuid != assetGuid) throw new InvalidOperationException("显隐配置的 PSD GUID 与文件名不一致。");
                _loaded.Add(config);
            }
            return config;
        }

        public PsdVisibilityState Load(string assetGuid) => Find(assetGuid)?.Read() ?? new PsdVisibilityState();
        public PsdExportState LoadExport(string assetGuid) => Find(assetGuid)?.ReadExport() ?? new PsdExportState();

        public void Save(string assetGuid, PsdVisibilityState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var current = Load(assetGuid);
            if (current.DocumentVisible == state.DocumentVisible && current.HiddenLayerIds.SequenceEqual(state.HiddenLayerIds)) return;
            SaveConfig(assetGuid, "PSD 工作台激活状态", config => config.Write(assetGuid, state));
        }

        public void SaveExport(string assetGuid, PsdExportState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (LoadExport(assetGuid).ExcludedLayerIds.SequenceEqual(state.ExcludedLayerIds)) return;
            SaveConfig(assetGuid, "PSD 工作台导出状态", config => config.WriteExport(assetGuid, state));
        }

        private void SaveConfig(string assetGuid, string undoName, Action<PsdVisibilityConfig> write)
        {
            var config = Find(assetGuid);
            if (config == null)
            {
                if (!AssetDatabase.IsValidFolder(ConfigFolder)) AssetDatabase.CreateFolder("Assets/PsdUiWorkbench", "Configs");
                config = ScriptableObject.CreateInstance<PsdVisibilityConfig>();
                config.Write(assetGuid, new PsdVisibilityState());
                AssetDatabase.CreateAsset(config, GetConfigPath(assetGuid));
                _loaded.Add(config);
            }
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(undoName);
            Undo.RecordObject(config, undoName);
            write(config);
            EditorUtility.SetDirty(config);
            QueueSave(config);
        }

        private void QueueSave(PsdVisibilityConfig config)
        {
            _pending.Add(config);
            _saveAfter = EditorApplication.timeSinceStartup + 0.3;
            if (_queued) return;
            _queued = true;
            EditorApplication.update += SaveWhenReady;
        }

        private void SaveWhenReady()
        {
            if (EditorApplication.timeSinceStartup >= _saveAfter) Flush();
        }

        public void Flush()
        {
            EditorApplication.update -= SaveWhenReady;
            _queued = false;
            Undo.FlushUndoRecordObjects();
            foreach (var config in _pending)
                if (config != null) AssetDatabase.SaveAssetIfDirty(config);
            _pending.Clear();
        }

        private void OnUndoRedo()
        {
            foreach (var config in _loaded)
                if (config != null && EditorUtility.IsDirty(config)) QueueSave(config);
            Changed?.Invoke();
        }

        public void Dispose()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
            Flush();
        }
    }
}
