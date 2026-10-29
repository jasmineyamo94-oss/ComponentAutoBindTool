using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using TestEnvironment = ComponentAutoBindTool.Tests.ViewCoreEnhancementTests.Environment;

namespace ComponentAutoBindTool.Tests
{
    public class ViewWaiterTests
    {
        private sealed class Observation
        {
            public bool Done;
            public bool Canceled;
            public Exception Error;
            public int ThreadId;
            public int CompletionCount;
        }

        private static async void Observe(Awaitable pending, Observation observation, Action onCompleted = null)
        {
            try
            {
                await pending;
                onCompleted?.Invoke();
            }
            catch (OperationCanceledException)
            {
                observation.Canceled = true;
            }
            catch (Exception exception)
            {
                observation.Error = exception;
            }
            finally
            {
                observation.ThreadId = Thread.CurrentThread.ManagedThreadId;
                observation.CompletionCount++;
                observation.Done = true;
            }
        }

        private static void AssertCompleted(Observation observation, bool canceled = false)
        {
            Assert.That(observation.Done, Is.True);
            Assert.That(observation.Error, Is.Null);
            Assert.That(observation.Canceled, Is.EqualTo(canceled));
            Assert.That(observation.CompletionCount, Is.EqualTo(1));
        }

        [Test]
        public void TwoIndependentWaitsOnOneWindowBothCompleteAtClose()
        {
            using var environment = new TestEnvironment();
            environment.Add("并发等待");
            int id = environment.Manager.ShowView("并发等待");
            var first = new Observation();
            var second = new Observation();
            Observe(environment.Manager.WaitViewClosedAsync(id), first);
            Observe(environment.Manager.WaitViewClosedAsync(id), second);

            Assert.That(first.Done || second.Done, Is.False);
            Assert.That(environment.Manager.CloseView(id), Is.True);
            AssertCompleted(first);
            AssertCompleted(second);
        }

        [Test]
        public void ManagerDisposeCompletesAllWaitsAcrossWindowsOnce()
        {
            using var environment = new TestEnvironment();
            environment.Add("甲");
            environment.Add("乙");
            int firstId = environment.Manager.ShowView("甲");
            int secondId = environment.Manager.ShowView("乙");
            var first = new Observation();
            var second = new Observation();
            var third = new Observation();
            Observe(environment.Manager.WaitViewClosedAsync(firstId), first);
            Observe(environment.Manager.WaitViewClosedAsync(firstId), second);
            Observe(environment.Manager.WaitViewClosedAsync(secondId), third);

            environment.Manager.Dispose();
            environment.Manager.Dispose();

            AssertCompleted(first);
            AssertCompleted(second);
            AssertCompleted(third);
        }

        [Test]
        public void ClosedAwaitableCanBeConsumedAfterTheInternalSourceIsReused()
        {
            using var environment = new TestEnvironment();
            environment.Add("延后消费");
            int id = environment.Manager.ShowView("延后消费");
            Awaitable oldWait = environment.Manager.WaitViewClosedAsync(id);
            environment.Manager.CloseView(id);
            Assert.That(environment.Manager.ShowView("延后消费"), Is.EqualTo(id));
            var newResult = new Observation();
            Observe(environment.Manager.WaitViewClosedAsync(id), newResult);
            var oldResult = new Observation();

            Observe(oldWait, oldResult);

            AssertCompleted(oldResult);
            Assert.That(newResult.Done, Is.False);
            environment.Manager.CloseView(id);
            AssertCompleted(newResult);
        }

        [Test]
        public void CanceledAwaitableCanBeConsumedAfterTheInternalSourceIsReused()
        {
            using var environment = new TestEnvironment();
            using var cancellation = new CancellationTokenSource();
            environment.Add("取消后消费");
            int id = environment.Manager.ShowView("取消后消费");
            Awaitable oldWait = environment.Manager.WaitViewClosedAsync(id, cancellation.Token);
            cancellation.Cancel();
            var newResult = new Observation();
            Observe(environment.Manager.WaitViewClosedAsync(id), newResult);
            var oldResult = new Observation();

            Observe(oldWait, oldResult);

            AssertCompleted(oldResult, true);
            Assert.That(newResult.Done, Is.False);
            environment.Manager.CloseView(id);
            AssertCompleted(newResult);
        }

        [Test]
        public void CloseContinuationReopensAndItsNewWaitRequiresTheNextClose()
        {
            using var environment = new TestEnvironment();
            environment.Add("重入等待");
            int id = environment.Manager.ShowView("重入等待");
            var first = new Observation();
            var reopened = new Observation();
            int reopenedId = 0;
            Observe(environment.Manager.WaitViewClosedAsync(id), first, () =>
            {
                reopenedId = environment.Manager.ShowView("重入等待");
                Observe(environment.Manager.WaitViewClosedAsync(reopenedId), reopened);
            });

            environment.Manager.CloseView(id);

            AssertCompleted(first);
            Assert.That(reopenedId, Is.EqualTo(id));
            Assert.That(environment.Manager.IsViewOpen("重入等待"), Is.True);
            Assert.That(reopened.Done, Is.False);
            environment.Manager.CloseView(reopenedId);
            AssertCompleted(reopened);
        }

        [Test]
        public void CancelingAnotherDetachedWaiterCannotCompleteAnUnrelatedNewWait()
        {
            using var environment = new TestEnvironment();
            using var cancellation = new CancellationTokenSource();
            environment.Add("旧批次");
            environment.Add("新租约");
            int oldId = environment.Manager.ShowView("旧批次");
            var first = new Observation();
            var canceled = new Observation();
            var renewed = new Observation();
            int newId = 0;
            Observe(environment.Manager.WaitViewClosedAsync(oldId), first, () =>
            {
                cancellation.Cancel();
                newId = environment.Manager.ShowView("新租约");
                Observe(environment.Manager.WaitViewClosedAsync(newId), renewed);
            });
            Observe(environment.Manager.WaitViewClosedAsync(oldId, cancellation.Token), canceled);

            environment.Manager.CloseView(oldId);

            AssertCompleted(first);
            AssertCompleted(canceled, true);
            Assert.That(renewed.Done, Is.False);
            environment.Manager.CloseView(newId);
            AssertCompleted(renewed);
        }

        [UnityTest]
        public IEnumerator BackgroundCancellationResumesThePublicWaitOnTheMainThread()
        {
            using var environment = new TestEnvironment();
            using var cancellation = new CancellationTokenSource();
            environment.Add("后台取消");
            int id = environment.Manager.ShowView("后台取消");
            int mainThreadId = Thread.CurrentThread.ManagedThreadId;
            var canceled = new Observation();
            var unaffected = new Observation();
            Observe(environment.Manager.WaitViewClosedAsync(id, cancellation.Token), canceled);
            Observe(environment.Manager.WaitViewClosedAsync(id), unaffected);
            Task cancelTask = Task.Run(() => cancellation.Cancel());
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while ((!cancelTask.IsCompleted || !canceled.Done) && timeout.Elapsed.TotalSeconds < 5)
            {
                yield return null;
            }

            Assert.That(cancelTask.IsCompleted, Is.True);
            Assert.That(cancelTask.Exception, Is.Null);
            AssertCompleted(canceled, true);
            Assert.That(canceled.ThreadId, Is.EqualTo(mainThreadId));
            Assert.That(unaffected.Done, Is.False);
            environment.Manager.CloseView(id);
            AssertCompleted(unaffected);
        }

        [Test]
        public void InvalidAndAlreadyClosedInstancesCompleteImmediately()
        {
            using var environment = new TestEnvironment();
            environment.Add("已关闭");
            int id = environment.Manager.ShowView("已关闭");
            environment.Manager.CloseView(id);
            var invalid = new Observation();
            var closed = new Observation();

            Observe(environment.Manager.WaitViewClosedAsync(0), invalid);
            Observe(environment.Manager.WaitViewClosedAsync(id), closed);

            AssertCompleted(invalid);
            AssertCompleted(closed);
        }
    }
}
