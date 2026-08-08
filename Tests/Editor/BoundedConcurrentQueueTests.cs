using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;

namespace CubicEngine.UnityCli.Tests
{
    internal sealed class BoundedConcurrentQueueTests
    {
        [Test]
        public void WorkerThreadEnqueueWaitsForMainThreadDrainAndRemainsBounded()
        {
            var queue = new BoundedConcurrentQueue<int>(3);
            var persisted = new List<int>();
            Exception workerException = null;
            var worker = new Thread(() =>
            {
                try
                {
                    for (var value = 1; value <= 5; value++)
                    {
                        queue.Enqueue(value);
                    }
                }
                catch (Exception exception)
                {
                    workerException = exception;
                }
            });

            worker.Start();
            Assert.That(worker.Join(TimeSpan.FromSeconds(2)), Is.True);

            Assert.That(workerException, Is.Null);
            Assert.That(persisted, Is.Empty, "Worker-thread logging must not persist directly.");
            Assert.That(queue.Count, Is.EqualTo(3));

            persisted.AddRange(queue.Drain());

            Assert.That(persisted, Is.EqualTo(new[] { 3, 4, 5 }));
            Assert.That(queue.Count, Is.Zero);
        }
    }
}
