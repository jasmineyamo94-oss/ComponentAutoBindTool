using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace PrefabAutoBind
{
    [FilePath("Library/PrefabAutoBind/WindowReloadState.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class PrefabAutoBindWindowReloadState : ScriptableSingleton<PrefabAutoBindWindowReloadState>
    {
        [SerializeField] private string configPath = string.Empty;
        [SerializeField] private PrefabAutoBindNodeId selectedNodeId;
        [SerializeField] private List<PrefabAutoBindNodeId> selectedNodeIds =
            new List<PrefabAutoBindNodeId>();
        [SerializeField] private bool reopenAfterReload;

        public string ConfigPath => configPath;

        public PrefabAutoBindNodeId SelectedNodeId => selectedNodeId;

        public IReadOnlyList<PrefabAutoBindNodeId> SelectedNodeIds => selectedNodeIds;

        public bool ReopenAfterReload => reopenAfterReload;

        public void SaveSelection(
            string nextConfigPath,
            PrefabAutoBindNodeId nextSelectedNodeId,
            IReadOnlyList<PrefabAutoBindNodeId> nextSelectedNodeIds)
        {
            configPath = nextConfigPath ?? string.Empty;
            selectedNodeId = nextSelectedNodeId;
            if (selectedNodeIds == null)
            {
                selectedNodeIds = new List<PrefabAutoBindNodeId>();
            }

            selectedNodeIds.Clear();
            if (nextSelectedNodeIds != null)
            {
                for (var index = 0; index < nextSelectedNodeIds.Count; index++)
                {
                    selectedNodeIds.Add(nextSelectedNodeIds[index]);
                }
            }

            Save(true);
        }

        public void ClearSelection()
        {
            configPath = string.Empty;
            selectedNodeId = default;
            selectedNodeIds?.Clear();
            Save(true);
        }

        public void SetReopenAfterReload(bool shouldReopen)
        {
            reopenAfterReload = shouldReopen;
            Save(true);
        }
    }

    [InitializeOnLoad]
    internal static class PrefabAutoBindWindowReloadBootstrap
    {
        static PrefabAutoBindWindowReloadBootstrap()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.delayCall += RestoreAfterReload;
        }

        private static void OnBeforeAssemblyReload()
        {
            var shouldReopen = Resources.FindObjectsOfTypeAll<PrefabAutoBindWindow>().Length > 0;
            PrefabAutoBindWindowReloadState.instance.SetReopenAfterReload(shouldReopen);
        }

        private static void RestoreAfterReload()
        {
            EditorApplication.delayCall -= RestoreAfterReload;

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += RestoreAfterReload;
                return;
            }

            var state = PrefabAutoBindWindowReloadState.instance;
            if (!state.ReopenAfterReload || string.IsNullOrWhiteSpace(state.ConfigPath))
            {
                return;
            }

            state.SetReopenAfterReload(false);
            var existingWindows = Resources.FindObjectsOfTypeAll<PrefabAutoBindWindow>();
            if (existingWindows.Length > 0)
            {
                for (var index = 0; index < existingWindows.Length; index++)
                {
                    existingWindows[index].RequestPersistedStateRestore();
                }

                return;
            }

            if (!ShouldOpenWindowAfterReload(
                    existingWindows.Length,
                    EditorApplication.isPlayingOrWillChangePlaymode))
            {
                return;
            }

            var window = PrefabAutoBindWindow.GetOrOpenWindow();
            window.RequestPersistedStateRestore();
        }

        internal static bool ShouldOpenWindowAfterReload(
            int existingWindowCount,
            bool isPlayingOrWillChangePlaymode)
        {
            return existingWindowCount == 0 && !isPlayingOrWillChangePlaymode;
        }
    }
}
