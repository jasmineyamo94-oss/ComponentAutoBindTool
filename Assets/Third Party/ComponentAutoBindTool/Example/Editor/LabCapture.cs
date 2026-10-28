using System;
using System.Collections.Generic;
using System.IO;
using ComponentAutoBindTool.ViewCore;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ComponentAutoBindTool.Example.Editor
{
    /// <summary>真实 Unity 渲染截图；批处理必须启用图形设备，不使用 -nographics。</summary>
    [InitializeOnLoad]
    public static class LabCapture
    {
        private static double m_ReadyAt;
        static LabCapture() { EditorApplication.update += Update; }
        public static void Run()
        {
            EditorSceneManager.OpenScene(AutoBindExampleSetup.ScenePath);
            SessionState.SetBool("AutoBind.Capture", true);
            EditorApplication.EnterPlaymode();
        }
        private static void Update()
        {
            if (!SessionState.GetBool("AutoBind.Capture", false) || !EditorApplication.isPlaying) return;
            var scope = Object.FindFirstObjectByType<AutoBindExampleScope>();
            if (scope == null || scope.Session == null) return;
            if (m_ReadyAt == 0) { m_ReadyAt = EditorApplication.timeSinceStartup + 1; return; }
            if (EditorApplication.timeSinceStartup < m_ReadyAt) return;
            SessionState.SetBool("AutoBind.Capture", false);
            try
            {
                Directory.CreateDirectory("Validation/FrameworkLab/Screenshots");
                var camera = new GameObject("CaptureCamera").AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color32(12, 19, 32, 255);
                camera.orthographic = true;
                camera.nearClipPlane = 0.1f; camera.farClipPlane = 100;
                var canvas = scope.Shell.GetComponentInParent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera; canvas.planeDistance = 10;
                var sizes = new[] { new Vector2Int(1280, 720), new Vector2Int(1920, 1080), new Vector2Int(2340, 1080), new Vector2Int(1440, 1080) };
                foreach (var size in sizes)
                {
                    scope.Shell.SelectExperiment(4);
                    int id = scope.Session.Open("Widgets");
                    ((AutoBindTestPresenter)scope.Telemetry.Windows[id].Presenter).Group.Left.Increment();
                    Capture(scope, canvas, camera, size, false);
                    Capture(scope, canvas, camera, size, true);
                    ExecuteEvents.Execute(scope.Shell.BoundView.btnReset.gameObject,
                        new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left }, ExecuteEvents.pointerClickHandler);
                    if (scope.Telemetry.Windows.Count != 0) throw new InvalidOperationException("Reset 点击没有清空实验。");
                }
                Debug.Log("FRAMEWORK_LAB_CAPTURE_PASS：4 种横屏比例，8 张真实渲染截图，Safe Area 与射线检查通过。");
                EditorApplication.Exit(0);
            }
            catch (Exception ex) { Debug.LogException(ex); EditorApplication.Exit(1); }
        }
        private static void Capture(AutoBindExampleScope scope, Canvas canvas, Camera camera, Vector2Int size, bool drawer)
        {
            var target = new RenderTexture(size.x, size.y, 24);
            camera.targetTexture = target;
            canvas.GetComponent<CanvasScaler>().enabled = false;
            canvas.scaleFactor = Mathf.Sqrt(size.x / 1280f * (size.y / 720f));
            var shell = scope.Shell;
            var ui = shell.BoundView;
            shell.ApplySafeArea(new Rect(size.x == 2340 ? 80 : 0, 0, size.x == 2340 ? size.x - 160 : size.x, size.y), size);
            shell.ShowDrawer(drawer);
            ui.rtfDrawer.gameObject.SetActive(drawer);
            ui.rtfDrawer.GetComponent<CanvasGroup>().alpha = drawer ? 1 : 0;
            ui.tMTxtTrace.text = scope.Telemetry.Describe();
            Canvas.ForceUpdateCanvases();
            ui.sRectTrace.content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Mathf.Max(ui.sRectTrace.viewport.rect.height, ui.tMTxtTrace.preferredHeight + 24));
            Canvas.ForceUpdateCanvases();
            var rect = (RectTransform)ui.btnReset.transform;
            var corners = new Vector3[4]; rect.GetWorldCorners(corners);
            foreach (var button in canvas.GetComponentsInChildren<Button>())
            {
                var hitRect = ((RectTransform)button.transform).rect;
                if (hitRect.height < 64 || hitRect.width < 64)
                    throw new InvalidOperationException(button.name + " 触控区域低于 64 参考像素。");
            }
            var position = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
            var results = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = position }, results);
            if (results.Count == 0 || results[0].gameObject != ui.btnReset.gameObject)
                throw new InvalidOperationException("Reset 按钮射线未命中。");
            camera.Render();
            RenderTexture.active = target;
            var image = new Texture2D(size.x, size.y, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, size.x, size.y), 0, 0); image.Apply();
            File.WriteAllBytes($"Validation/FrameworkLab/Screenshots/{size.x}x{size.y}-{(drawer ? "observer" : "widgets")}.png", image.EncodeToPNG());
            if (drawer && ui.sRectTrace.content.rect.height > ui.sRectTrace.viewport.rect.height)
            {
                var before = ui.sRectTrace.content.anchoredPosition;
                ui.sRectTrace.OnScroll(new PointerEventData(EventSystem.current) { scrollDelta = new Vector2(0, -3) });
                if (ui.sRectTrace.content.anchoredPosition == before) throw new InvalidOperationException("观察抽屉无法滚动。");
                ui.sRectTrace.verticalNormalizedPosition = 1;
            }
            RenderTexture.active = null; camera.targetTexture = null;
            Object.DestroyImmediate(image); Object.DestroyImmediate(target);
        }
    }
}
