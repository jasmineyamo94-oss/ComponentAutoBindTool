using UnityEngine;
using TMPro;
using UnityEngine.UI;
namespace ComponentAutoBindTool.Example
{
    [RequireComponent(typeof(Core.ComponentAutoBindTool))]
    public partial class LabShellView : MonoBehaviour
    {
        private LabSession _session;
        private LabTelemetry _trace;
        private Button[] _actions;
        private Button[] _navigation;
        private CanvasGroup _drawer;
        private bool _drawerOpen;
        private float _refresh;
        private Rect _lastSafeArea;
        public UIView BoundView { get { EnsureAutoBind(gameObject); return _view; } }
        public void Initialize(LabSession session, LabTelemetry trace)
        {
            EnsureAutoBind(gameObject);
            _session = session;
            _trace = trace;
            _actions = new[] { _view.btnAction1, _view.btnAction2, _view.btnAction3, _view.btnAction4, _view.btnAction5, _view.btnAction6 };
            _navigation = new[] { _view.btnNav1, _view.btnNav2, _view.btnNav3, _view.btnNav4, _view.btnNav5 };
            for (int i = 0; i < _actions.Length; i++)
            { int index = i; _actions[i].onClick.AddListener(() => _session.Act(index)); }
            for (int i = 0; i < _navigation.Length; i++)
            { int index = i; _navigation[i].onClick.AddListener(() => SelectExperiment(index)); }
            _view.btnReset.onClick.AddListener(() => SelectExperiment(_session.Experiment));
            _view.btnObserve.onClick.AddListener(() => ShowDrawer(!_drawerOpen));
            _drawer = _view.rtfDrawer.GetComponent<CanvasGroup>();
            _drawer.alpha = 0;
            SetLabels(0);
        }
        public void SelectExperiment(int index) { _session.Reset(index); SetLabels(index); }
        private void SetLabels(int index)
        {
            _view.tMTxtTopic.text = LabSession.Titles[index];
            _view.tMTxtHint.text = LabSession.Hints[index];
            for (int i = 0; i < _actions.Length; i++)
                _actions[i].GetComponentInChildren<TMP_Text>().text = LabSession.Captions[index][i];
            for (int i = 0; i < _navigation.Length; i++)
                _navigation[i].image.color = i == index ? new Color32(29, 119, 125, 255) : new Color32(40, 66, 85, 255);
        }
        public void ShowDrawer(bool visible)
        {
            _drawerOpen = visible;
            if (visible) _view.rtfDrawer.gameObject.SetActive(true);
            _view.btnObserve.GetComponentInChildren<TMP_Text>().text = visible ? "收起观察状态" : "观察运行状态";
        }
        public void ApplySafeArea(Rect safe, Vector2 screen)
        {
            if (screen.x <= 0 || screen.y <= 0) return;
            var rect = (RectTransform)transform;
            rect.anchorMin = new Vector2(safe.xMin / screen.x, safe.yMin / screen.y);
            rect.anchorMax = new Vector2(safe.xMax / screen.x, safe.yMax / screen.y);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            _view.rtfPortrait.gameObject.SetActive(screen.y > screen.x);
        }
        private void Update()
        {
            if (_session == null) return;
            if (_lastSafeArea != Screen.safeArea)
            { _lastSafeArea = Screen.safeArea; ApplySafeArea(_lastSafeArea, new Vector2(Screen.width, Screen.height)); }
            _drawer.alpha = Mathf.MoveTowards(_drawer.alpha, _drawerOpen ? 1 : 0, Time.unscaledDeltaTime / 0.18f);
            if (!_drawerOpen && _drawer.alpha == 0) _view.rtfDrawer.gameObject.SetActive(false);
            _refresh += Time.unscaledDeltaTime;
            if (!_drawerOpen || _refresh < 0.1f) return;
            _refresh = 0;
            _view.tMTxtTrace.text = _trace.Describe();
            _view.sRectTrace.content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,
                Mathf.Max(_view.sRectTrace.viewport.rect.height, _view.tMTxtTrace.preferredHeight + 24));
        }
    }
}
