using UnityEngine;
using TMPro;
using UnityEngine.UI;
namespace ComponentAutoBindTool.Example
{
    [RequireComponent(typeof(Core.ComponentAutoBindTool))]
    public partial class LabShellView : MonoBehaviour
    {
        private LabSession m_Session;
        private LabTelemetry m_Trace;
        private Button[] m_Actions;
        private Button[] m_Navigation;
        private CanvasGroup m_Drawer;
        private bool m_DrawerOpen;
        private float m_Refresh;
        private Rect m_LastSafeArea;
        public UIView BoundView { get { EnsureAutoBind(gameObject); return view; } }
        public void Initialize(LabSession session, LabTelemetry trace)
        {
            EnsureAutoBind(gameObject);
            m_Session = session;
            m_Trace = trace;
            m_Actions = new[] { view.btnAction1, view.btnAction2, view.btnAction3, view.btnAction4, view.btnAction5, view.btnAction6 };
            m_Navigation = new[] { view.btnNav1, view.btnNav2, view.btnNav3, view.btnNav4, view.btnNav5 };
            for (int i = 0; i < m_Actions.Length; i++)
            { int index = i; m_Actions[i].onClick.AddListener(() => m_Session.Act(index)); }
            for (int i = 0; i < m_Navigation.Length; i++)
            { int index = i; m_Navigation[i].onClick.AddListener(() => SelectExperiment(index)); }
            view.btnReset.onClick.AddListener(() => SelectExperiment(m_Session.Experiment));
            view.btnObserve.onClick.AddListener(() => ShowDrawer(!m_DrawerOpen));
            m_Drawer = view.rtfDrawer.GetComponent<CanvasGroup>();
            m_Drawer.alpha = 0;
            SetLabels(0);
        }
        public void SelectExperiment(int index) { m_Session.Reset(index); SetLabels(index); }
        private void SetLabels(int index)
        {
            view.tMTxtTopic.text = LabSession.Titles[index];
            view.tMTxtHint.text = LabSession.Hints[index];
            for (int i = 0; i < m_Actions.Length; i++)
                m_Actions[i].GetComponentInChildren<TMP_Text>().text = LabSession.Captions[index][i];
            for (int i = 0; i < m_Navigation.Length; i++)
                m_Navigation[i].image.color = i == index ? new Color32(29, 119, 125, 255) : new Color32(40, 66, 85, 255);
        }
        public void ShowDrawer(bool visible)
        {
            m_DrawerOpen = visible;
            if (visible) view.rtfDrawer.gameObject.SetActive(true);
            view.btnObserve.GetComponentInChildren<TMP_Text>().text = visible ? "收起观察状态" : "观察运行状态";
        }
        public void ApplySafeArea(Rect safe, Vector2 screen)
        {
            if (screen.x <= 0 || screen.y <= 0) return;
            var rect = (RectTransform)transform;
            rect.anchorMin = new Vector2(safe.xMin / screen.x, safe.yMin / screen.y);
            rect.anchorMax = new Vector2(safe.xMax / screen.x, safe.yMax / screen.y);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            view.rtfPortrait.gameObject.SetActive(screen.y > screen.x);
        }
        private void Update()
        {
            if (m_Session == null) return;
            if (m_LastSafeArea != Screen.safeArea)
            { m_LastSafeArea = Screen.safeArea; ApplySafeArea(m_LastSafeArea, new Vector2(Screen.width, Screen.height)); }
            m_Drawer.alpha = Mathf.MoveTowards(m_Drawer.alpha, m_DrawerOpen ? 1 : 0, Time.unscaledDeltaTime / 0.18f);
            if (!m_DrawerOpen && m_Drawer.alpha == 0) view.rtfDrawer.gameObject.SetActive(false);
            m_Refresh += Time.unscaledDeltaTime;
            if (!m_DrawerOpen || m_Refresh < 0.1f) return;
            m_Refresh = 0;
            view.tMTxtTrace.text = m_Trace.Describe();
            view.sRectTrace.content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,
                Mathf.Max(view.sRectTrace.viewport.rect.height, view.tMTxtTrace.preferredHeight + 24));
        }
    }
}
