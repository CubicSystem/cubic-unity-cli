using System;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace CubicEngine.UnityCli.Tests
{
    internal sealed class ConsoleStoreTests
    {
        [Test]
        public void WorkerThreadLogIsVisibleOnlyAfterMainThreadDrain()
        {
            ConsoleStore.DrainPendingLogs();
            var message = "cubic-cli-threaded-log-test-" + Guid.NewGuid().ToString("N");
            Exception workerException = null;
            var worker = new Thread(() =>
            {
                try
                {
                    ConsoleStore.EnqueueLog(message, "test stack", LogType.Exception);
                }
                catch (Exception exception)
                {
                    workerException = exception;
                }
            });

            worker.Start();
            Assert.That(worker.Join(TimeSpan.FromSeconds(2)), Is.True);

            Assert.That(workerException, Is.Null);
            Assert.That(ConsoleStore.Read(limit: 250).Any(entry => entry.message == message), Is.False);
            Assert.That(ConsoleStore.PendingCount, Is.GreaterThanOrEqualTo(1));

            ConsoleStore.DrainPendingLogs();

            Assert.That(ConsoleStore.Read(limit: 250).Any(entry => entry.message == message), Is.True);
        }
    }
}
