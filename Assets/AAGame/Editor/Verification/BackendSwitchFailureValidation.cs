using System;
using System.IO;
using System.Linq;
using AAGame.Assets;
using AAGame.Editor.EnvironmentSetup;
using UnityEditor;
using UnityEngine;

/// <summary>只在显式启动后进行真实安装及回滚；跨重载观察故障、取消和重复请求。</summary>
[InitializeOnLoad]
public static class BackendSwitchFailureValidation
{
    private const string Prefix = "ProjectTools.SwitchFailureValidation.";
    private const string FaultKey = "ProjectTools.BackendSwitch.FaultStage";
    private const string Evidence = "Library/AAGameValidation/switch-failures.txt";
    private static readonly string[] Stages = { "archive", "remove", "restore", "build", "commit", "installWait" };
    private static double _nextTick;
    public static bool IsRunning => SessionState.GetBool(Prefix + "Active", false);
    public static string Status => SessionState.GetString(Prefix + "Status", "尚未运行");

    static BackendSwitchFailureValidation() => EditorApplication.update += Update;

    public static void Start()
    {
        if (IsRunning || BackendSwitchCoordinator.IsBusy || BackendSwitchCoordinator.RecoveryRequired ||
            EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("请先退出 Play 并完成已有切换或恢复任务。");
        CheckResources();
        SessionState.SetInt(Prefix + "Index", 0);
        SessionState.SetString(Prefix + "Phase", "begin");
        SessionState.SetBool(Prefix + "Active", true);
        ResetDeadline();
        Directory.CreateDirectory(Path.GetDirectoryName(Evidence));
        File.WriteAllText(Evidence, DateTime.UtcNow.ToString("O") + " 开始真实切换故障恢复验证\n");
        SetStatus("等待环境检测结束后验证归档阶段故障");
    }

    private static void Update()
    {
        if (!IsRunning || EditorApplication.isCompiling || EditorApplication.isUpdating ||
            EditorApplication.timeSinceStartup < _nextTick) return;
        _nextTick = EditorApplication.timeSinceStartup + .3;
        try
        {
            if (DateTime.UtcNow.Ticks > long.Parse(SessionState.GetString(Prefix + "Deadline", "0")))
                throw new TimeoutException("当前故障恢复验证超过 20 分钟。");
            int index = SessionState.GetInt(Prefix + "Index", 0);
            string phase = SessionState.GetString(Prefix + "Phase", "");
            if (phase == "begin")
            {
                if (EnvironmentInstaller.IsBusy || BackendSwitchCoordinator.IsBusy) return;
                CheckResources();
                if (index > Stages.Length)
                {
                    Complete(true, "PASS：安装结果、归档、卸载、恢复、构建、提交故障全部回滚；取消及重复请求验证通过。");
                    return;
                }
                if (index < Stages.Length)
                {
                    SessionState.SetString(FaultKey, Stages[index]);
                    SetStatus("验证故障阶段：" + Stages[index]);
                }
                else
                {
                    SessionState.EraseString(FaultKey);
                    SetStatus("验证已启动安装的取消和重复请求");
                }
                // 先持久化等待阶段，避免 UPM 重载后重复发起同一事务。
                SessionState.SetString(Prefix + "Phase", index == Stages.Length ? "awaitCancel" : "wait");
                ResetDeadline();
                BackendSwitchCoordinator.Start(AssetBackendKind.Addressables, false);
                if (!BackendSwitchCoordinator.IsBusy)
                    throw new InvalidOperationException("故障事务没有启动。");
                if (index == Stages.Length)
                {
                    bool rejected = false;
                    try { BackendSwitchCoordinator.Start(AssetBackendKind.YooAsset, false); }
                    catch (InvalidOperationException) { rejected = true; }
                    if (!rejected) throw new InvalidOperationException("重复切换请求没有被拒绝。");
                    Append("PASS：切换期间的重复请求被拒绝。");
                }
                return;
            }
            if (phase == "awaitCancel")
            {
                if (!BackendSwitchCoordinator.IsBusy)
                    throw new InvalidOperationException("安装尚未进入取消验证便提前结束：" + BackendSwitchCoordinator.LastError);
                // 明确等到环境安装已启动，再提出取消；协调器负责观察原生操作结束。
                if (!EnvironmentInstaller.IsBusy ||
                    EnvironmentInstallState.instance.Phase != "verify") return;
                SessionState.SetString(Prefix + "Phase", "wait");
                BackendSwitchCoordinator.Cancel();
                SetStatus("等待已启动安装结束并回滚");
                return;
            }
            if (phase == "wait")
            {
                if (BackendSwitchCoordinator.IsBusy || EnvironmentInstaller.IsBusy) return;
                if (BackendSwitchCoordinator.RecoveryRequired)
                    throw new InvalidOperationException("回滚尚待恢复：" + BackendSwitchCoordinator.LastError);
                if (string.IsNullOrEmpty(BackendSwitchCoordinator.LastError))
                    throw new InvalidOperationException("预期故障或取消未触发，不能视为通过。");
                if (index < Stages.Length)
                {
                    if (!BackendSwitchCoordinator.LastError.Contains("验证注入的切换故障：" + Stages[index]))
                        throw new InvalidOperationException("事务在预期阶段之前失败：" + BackendSwitchCoordinator.LastError);
                    if (!string.IsNullOrEmpty(SessionState.GetString(FaultKey, "")))
                        throw new InvalidOperationException("阶段故障没有被消费。");
                }
                else if (!BackendSwitchCoordinator.LastError.Contains("取消"))
                    throw new InvalidOperationException("事务失败原因不是取消：" + BackendSwitchCoordinator.LastError);
                CheckResources();
                Append("PASS：" + (index < Stages.Length ? Stages[index] : "cancel") +
                    "；原 Resources 配置恢复，实际 SDK 为零，无待恢复任务。");
                SessionState.SetInt(Prefix + "Index", index + 1);
                SessionState.SetString(Prefix + "Phase", "begin");
                ResetDeadline();
                return;
            }
            throw new InvalidOperationException("未知故障验证阶段：" + phase);
        }
        catch (Exception error)
        {
            SessionState.EraseString(FaultKey);
            Complete(false, "FAIL：" + error);
            Debug.LogException(error);
        }
    }

    private static void CheckResources()
    {
        if (DemoResourceSetup.EnsureSettings().Backend != AssetBackendKind.Resources)
            throw new InvalidOperationException("故障验证要求起点和回滚终点均为 Resources。");
        var names = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Select(p => p.name).ToArray();
        if (new[] { "com.unity.addressables", "com.unity.addressables.android", "com.tuyoogame.yooasset" }.Any(names.Contains))
            throw new InvalidOperationException("Resources 状态仍安装了资源 SDK，不能作为互斥回滚基线。");
    }

    private static void ResetDeadline() => SessionState.SetString(Prefix + "Deadline", DateTime.UtcNow.AddMinutes(20).Ticks.ToString());
    private static void SetStatus(string value) => SessionState.SetString(Prefix + "Status", value);
    private static void Append(string value) => File.AppendAllText(Evidence, DateTime.UtcNow.ToString("O") + " " + value + "\n");
    private static void Complete(bool success, string message)
    {
        Append(message);
        SetStatus(message);
        SessionState.SetBool(Prefix + "Active", false);
        SessionState.SetBool(Prefix + "Succeeded", success);
        Debug.Log("[BackendSwitchFailureValidation] " + (success ? "PASS" : "FAIL"));
    }
}
