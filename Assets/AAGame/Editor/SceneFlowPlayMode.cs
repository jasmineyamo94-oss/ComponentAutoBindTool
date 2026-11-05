using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class SceneFlowPlayMode
{
    static SceneFlowPlayMode() => EditorApplication.playModeStateChanged += Changed;

    private static void Changed(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode)
        {
            string name = SceneManager.GetActiveScene().name;
            if (name != "StartScene" && name != "MainScene" && name != "LevelScene")
                return;
            SessionState.SetBool("AAGame.OwnedPlayOverride", true);
            SessionState.SetString("AAGame.EditorStage", name);
            SessionState.SetString(
                "AAGame.PreviousPlayScene",
                AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene)
            );
            EditorSceneManager.playModeStartScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(
                "Assets/AAGame/Scenes/BootstrapScene.unity"
            );
        }
        if (state == PlayModeStateChange.EnteredEditMode)
        {
            string previous = SessionState.GetString("AAGame.PreviousPlayScene", "");
            if (SessionState.GetBool("AAGame.OwnedPlayOverride", false))
                EditorSceneManager.playModeStartScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(
                    previous
                );
            SessionState.EraseBool("AAGame.OwnedPlayOverride");
            SessionState.EraseString("AAGame.EditorStage");
            SessionState.EraseString("AAGame.PreviousPlayScene");
        }
    }
}
