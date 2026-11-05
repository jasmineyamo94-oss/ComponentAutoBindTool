using System.Collections.Generic;
using ComponentAutoBindTool.ViewCore;
using UnityEngine;
using UnityEngine.UI;

public sealed class StageViewRootHelper : MonoBehaviour, IViewRootHelper
{
    // 保存组件恢复时的启用状态；射线检测的期望状态可在挂起期间更新。
    private readonly Dictionary<Canvas, bool> _canvases = new();
    private readonly Dictionary<GraphicRaycaster, bool> _raycasters = new();
    // 复用扫描缓冲区，避免每次收集子组件时新建列表。
    private readonly List<Canvas> _canvasBuffer = new();
    private readonly List<GraphicRaycaster> _rayBuffer = new();
    private CanvasGroup _group;
    public bool IsSuspended => _suspended;
    private bool _suspended;
    private float _alpha;
    private bool _interactable, _blocks;

    // 此场景的窗口共用当前根节点，不按名称或层级区分挂载点。
    public Transform GetViewRoot(string name, UILayer layer = UILayer.Normal) => transform;

    public void Observe(IWindowContext context)
    {
        // 挂起期间观察到的 WindowEntry 也要隐藏并禁用射线检测。
        if (_suspended)
            Capture((context as WindowEntry)?.GameObject);
    }

    public void SetDesiredInteractable(GraphicRaycaster raycaster, bool enabled)
    {
        if (_suspended)
        {
            // 记录窗口期望的交互状态，但挂起期间始终保持射线检测关闭。
            _raycasters[raycaster] = enabled;
            raycaster.enabled = false;
        }
        else
            raycaster.enabled = enabled;
    }

    private void Capture(GameObject host)
    {
        if (host == null)
            return;
        // 包含未激活的子节点；重复扫描时保留已记录的状态。
        host.GetComponentsInChildren(true, _canvasBuffer);
        foreach (var c in _canvasBuffer)
        {
            if (!_canvases.ContainsKey(c))
                _canvases.Add(c, c.enabled);
            c.enabled = false;
        }
        host.GetComponentsInChildren(true, _rayBuffer);
        foreach (var r in _rayBuffer)
        {
            if (!_raycasters.ContainsKey(r))
                _raycasters.Add(r, r.enabled);
            r.enabled = false;
        }
    }

    public void Suspend()
    {
        if (_suspended)
            return;
        _suspended = true;
        // 先保存根节点的显示和交互状态，再整体隐藏并阻止输入。
        _group = GetComponent<CanvasGroup>();
        _alpha = _group.alpha;
        _interactable = _group.interactable;
        _blocks = _group.blocksRaycasts;
        _group.alpha = 0;
        _group.interactable = false;
        _group.blocksRaycasts = false;
        // 单独关闭子 Canvas 和射线检测，并保存它们各自的状态。
        Capture(gameObject);
    }

    public void Resume()
    {
        if (!_suspended)
            return;
        _suspended = false;
        // 窗口可能在挂起期间被销毁，跳过失效组件并还原其余状态。
        foreach (var p in _canvases)
            if (p.Key != null)
                p.Key.enabled = p.Value;
        foreach (var p in _raycasters)
            if (p.Key != null)
                p.Key.enabled = p.Value;
        _canvases.Clear();
        _raycasters.Clear();
        _group.alpha = _alpha;
        _group.interactable = _interactable;
        _group.blocksRaycasts = _blocks;
    }
}
