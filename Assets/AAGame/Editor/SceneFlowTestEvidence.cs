using System.IO;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

[InitializeOnLoad]
internal sealed class SceneFlowTestEvidence : ICallbacks
{
    static SceneFlowTestEvidence()
    {
        var api = ScriptableObject.CreateInstance<TestRunnerApi>();
        api.RegisterCallbacks(new SceneFlowTestEvidence());
    }

    public void RunStarted(ITestAdaptor tests) { }

    public void TestStarted(ITestAdaptor test) { }

    public void TestFinished(ITestResultAdaptor result)
    {
        if (result.Test.IsSuite || result.TestStatus != TestStatus.Failed)
            return;
        Directory.CreateDirectory("Temp/SceneFlow");
        File.AppendAllText(
            "Temp/SceneFlow/test-failures.txt",
            result.Test.FullName + "\n" + result.Message + "\n" + result.StackTrace + "\n"
        );
    }

    public void RunFinished(ITestResultAdaptor result)
    {
        Directory.CreateDirectory("Temp/SceneFlow");
        TestRunnerApi.SaveResultToFile(result, "Temp/SceneFlow/regression.xml");
    }
}
