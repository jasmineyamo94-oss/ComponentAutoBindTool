using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace PrefabAutoBind
{
    [Serializable]
    internal sealed class PrefabAutoBindPendingBindQueue
    {
        [SerializeField] private List<string> configGuids = new List<string>();

        public int Count => configGuids != null ? configGuids.Count : 0;

        public bool Enqueue(string configGuid)
        {
            if (string.IsNullOrWhiteSpace(configGuid))
            {
                return false;
            }

            EnsureList();
            if (configGuids.Contains(configGuid))
            {
                return false;
            }

            configGuids.Add(configGuid);
            return true;
        }

        public bool Remove(string configGuid)
        {
            return configGuids != null && configGuids.Remove(configGuid);
        }

        public string GetAt(int index)
        {
            return configGuids != null && index >= 0 && index < configGuids.Count
                ? configGuids[index]
                : null;
        }

        private void EnsureList()
        {
            if (configGuids == null)
            {
                configGuids = new List<string>();
            }
        }
    }

    [FilePath(
        "Library/PrefabAutoBind/PendingDirectReferenceBinds.asset",
        FilePathAttribute.Location.ProjectFolder)]
    internal sealed class PrefabAutoBindPendingBindState :
        ScriptableSingleton<PrefabAutoBindPendingBindState>
    {
        [SerializeField] private PrefabAutoBindPendingBindQueue queue =
            new PrefabAutoBindPendingBindQueue();

        public int Count => Queue.Count;

        public bool Enqueue(string configGuid)
        {
            if (!Queue.Enqueue(configGuid))
            {
                return false;
            }

            Save(true);
            return true;
        }

        public bool Remove(string configGuid)
        {
            if (!Queue.Remove(configGuid))
            {
                return false;
            }

            Save(true);
            return true;
        }

        public string GetAt(int index)
        {
            return Queue.GetAt(index);
        }

        private PrefabAutoBindPendingBindQueue Queue
        {
            get
            {
                if (queue == null)
                {
                    queue = new PrefabAutoBindPendingBindQueue();
                }

                return queue;
            }
        }
    }

    [InitializeOnLoad]
    internal static class PrefabAutoBindDirectReferenceBindScheduler
    {
        private static bool processingQueued;

        static PrefabAutoBindDirectReferenceBindScheduler()
        {
            QueueProcessing();
        }

        public static bool TrySchedule(PrefabAutoBindConfig config, out string errorMessage)
        {
            errorMessage = null;
            if (config == null)
            {
                errorMessage = "DirectReference automatic binding failed: configuration is missing.";
                return false;
            }

            var configPath = AssetDatabase.GetAssetPath(config);
            var configGuid = string.IsNullOrWhiteSpace(configPath)
                ? string.Empty
                : AssetDatabase.AssetPathToGUID(configPath);
            if (string.IsNullOrWhiteSpace(configGuid))
            {
                errorMessage = "DirectReference automatic binding failed: configuration must be saved as a project asset.";
                return false;
            }

            PrefabAutoBindPendingBindState.instance.Enqueue(configGuid);
            QueueProcessing();
            return true;
        }

        internal static bool TryResolveConfig(
            string configGuid,
            out PrefabAutoBindConfig config,
            out string errorMessage)
        {
            config = null;
            errorMessage = null;
            if (string.IsNullOrWhiteSpace(configGuid))
            {
                errorMessage = "DirectReference automatic binding failed: pending configuration identifier is empty.";
                return false;
            }

            var configPath = AssetDatabase.GUIDToAssetPath(configGuid);
            if (string.IsNullOrWhiteSpace(configPath))
            {
                errorMessage = "DirectReference automatic binding failed: pending configuration was deleted or cannot be resolved after being moved.";
                return false;
            }

            config = AssetDatabase.LoadAssetAtPath<PrefabAutoBindConfig>(configPath);
            if (config == null)
            {
                errorMessage = "DirectReference automatic binding failed: unable to load configuration '" + configPath + "'.";
                return false;
            }

            return true;
        }

        private static void QueueProcessing()
        {
            if (processingQueued)
            {
                return;
            }

            processingQueued = true;
            EditorApplication.delayCall += ProcessPending;
        }

        private static void ProcessPending()
        {
            EditorApplication.delayCall -= ProcessPending;
            processingQueued = false;

            var state = PrefabAutoBindPendingBindState.instance;
            if (state.Count == 0)
            {
                return;
            }

            if (EditorApplication.isCompiling ||
                EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode)
            {
                QueueProcessing();
                return;
            }

            var configGuid = state.GetAt(0);
            PrefabAutoBindResult result;
            if (EditorUtility.scriptCompilationFailed)
            {
                result = new PrefabAutoBindResult(
                    false,
                    "DirectReference automatic binding failed: C# script compilation failed. Fix the compilation errors in the Console and export again.");
            }
            else if (!TryResolveConfig(configGuid, out var config, out var errorMessage))
            {
                result = new PrefabAutoBindResult(false, errorMessage);
            }
            else
            {
                result = PrefabAutoBindCodeGenerator.BindDirectReferences(config);
            }

            state.Remove(configGuid);
            NotifyResult(result);

            if (state.Count > 0)
            {
                QueueProcessing();
            }
        }

        private static void NotifyResult(PrefabAutoBindResult result)
        {
            if (result.Success)
            {
                Debug.Log(result.Message);
            }
            else
            {
                Debug.LogError(result.Message);
            }

            if (!Application.isBatchMode)
            {
                EditorUtility.DisplayDialog(
                    result.Success ? "Export Complete" : "Export Failed",
                    result.Message,
                    "OK");
            }
        }
    }
}
