using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using NUnit.Framework;

namespace CubicEngine.UnityCli.Tests
{
    internal sealed class HeartbeatServiceTests
    {
        [Serializable]
        private sealed class InstanceContract
        {
            public int port;
            public string url;
            public string updatedAtUtc;
        }

        [Serializable]
        private sealed class ConnectionContract
        {
            public bool ready;
            public bool busy;
            public string busyCommand;
            public string busyRequestId;
            public long busyDurationMs;
            public bool busyStale;
            public long busyStaleAfterMs;
            public int queuedCommands;
        }

        [Serializable]
        private sealed class StatusContract
        {
            public bool ready;
            public bool reloading;
            public bool busy;
            public string busyCommand;
            public bool busyStale;
            public int queuedCommands;
            public string message;
            public string updatedAtUtc;
            public string lastUpdatedUtc;
            public ConnectionContract connection;
        }

        private string _directory;
        private string _statusPath;
        private string _instancePath;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "cubic-cli-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _statusPath = Path.Combine(_directory, "status.json");
            _instancePath = Path.Combine(_directory, "instance.json");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }

        [Test]
        public void RepeatedEditorUpdatesPublishOnlyOnceWithinOneSecond()
        {
            var pump = new HeartbeatPump(1.0d);
            var publicationCount = 0;

            for (var update = 0; update < 10000; update++)
            {
                if (pump.Pump(100d + update / 10001d, publicationEnabled: true))
                {
                    publicationCount++;
                }
            }

            Assert.That(publicationCount, Is.EqualTo(1),
                "The editor update hot path must not publish more than once inside the heartbeat interval.");
            Assert.That(pump.Pump(101d, publicationEnabled: true), Is.True);
        }

        [Test]
        public void ForceRefreshPublishesImmediatelyAndDefersPeriodicDuplicate()
        {
            var pump = new HeartbeatPump(1.0d);

            Assert.That(pump.Pump(10d, publicationEnabled: true), Is.True);
            Assert.That(pump.Pump(10.25d, publicationEnabled: true), Is.False);
            Assert.That(pump.ForceRefresh(10.25d, publicationEnabled: true), Is.True,
                "An explicit refresh must publish even when the periodic heartbeat is not due.");
            Assert.That(pump.Pump(11.249d, publicationEnabled: true), Is.False,
                "The update immediately following a force refresh must not duplicate it.");
            Assert.That(pump.Pump(11.25d, publicationEnabled: true), Is.True);
        }

        [Test]
        public void UnchangedInstanceIsNotRewrittenIncludingAfterDomainReload()
        {
            var writeCount = 0;
            Func<string, string, Encoding, bool> countingWrite = (path, contents, encoding) =>
            {
                writeCount++;
                return AtomicFileWriter.TryWriteAllText(path, contents, encoding);
            };
            var identity = CreateIdentity(48061);
            var store = CreateStore("2026-08-13T00:00:00.0000000Z", countingWrite);

            Assert.That(store.PublishInstance(identity), Is.EqualTo(InstancePublicationResult.Published));
            var originalJson = File.ReadAllText(_instancePath);
            Assert.That(store.PublishInstance(identity), Is.EqualTo(InstancePublicationResult.Unchanged));
            Assert.That(writeCount, Is.EqualTo(1));

            var reloadedStore = CreateStore("2026-08-13T00:00:01.0000000Z", countingWrite);
            Assert.That(reloadedStore.PublishInstance(identity), Is.EqualTo(InstancePublicationResult.Unchanged),
                "A new publication store must recognize the unchanged advertisement already on disk.");
            Assert.That(writeCount, Is.EqualTo(1));
            Assert.That(File.ReadAllText(_instancePath), Is.EqualTo(originalJson));
        }

        [Test]
        public void ReconnectPublishesChangedEndpointAndConnectionTimestamp()
        {
            var timestamps = new[]
            {
                "2026-08-13T00:00:00.0000000Z",
                "2026-08-13T00:01:00.0000000Z"
            };
            var timestampIndex = 0;
            var store = new HeartbeatPublicationStore(
                _statusPath,
                _instancePath,
                new UTF8Encoding(false),
                () => timestamps[timestampIndex++]);

            Assert.That(store.PublishInstance(CreateIdentity(48061)),
                Is.EqualTo(InstancePublicationResult.Published));
            Assert.That(store.PublishInstance(CreateIdentity(48062)),
                Is.EqualTo(InstancePublicationResult.Published));

            var instance = JsonConvert.DeserializeObject<InstanceContract>(File.ReadAllText(_instancePath));
            Assert.That(instance.port, Is.EqualTo(48062));
            Assert.That(instance.url, Is.EqualTo("http://127.0.0.1:48062"));
            Assert.That(instance.updatedAtUtc, Is.EqualTo(timestamps[1]));
        }

        [Test]
        public void ReloadStatusPreservesTheExistingInstanceAdvertisement()
        {
            var store = CreateStore("2026-08-13T00:00:00.0000000Z");
            var identity = CreateIdentity(48061);
            Assert.That(store.PublishInstance(identity), Is.EqualTo(InstancePublicationResult.Published));
            var instanceBeforeReload = File.ReadAllText(_instancePath);

            Assert.That(store.PublishStatus(new
            {
                ready = false,
                reloading = true,
                message = "Unity is reconnecting after assembly reload.",
                connection = new { ready = false, reloading = true }
            }), Is.True);
            Assert.That(store.PublishInstance(identity), Is.EqualTo(InstancePublicationResult.Unchanged));

            var status = JsonConvert.DeserializeObject<StatusContract>(File.ReadAllText(_statusPath));
            Assert.That(status.ready, Is.False);
            Assert.That(status.reloading, Is.True);
            Assert.That(status.message, Is.EqualTo("Unity is reconnecting after assembly reload."));
            Assert.That(File.ReadAllText(_instancePath), Is.EqualTo(instanceBeforeReload));
        }

        [Test]
        public void BusyKeepAlivePublishesCanonicalStatusContract()
        {
            const string updatedAtUtc = "2026-08-13T00:00:03.0000000Z";
            var cached = new
            {
                ready = true,
                busy = false,
                connection = new { ready = true, busy = false }
            };
            var activity = new CommandActivitySnapshot
            {
                busy = true,
                stale = true,
                command = "test.run",
                requestId = "request-123",
                startedAtUtc = "2026-08-13T00:00:00.0000000Z",
                durationMs = 31000,
                staleAfterMs = 30000,
                queuedCount = 1,
                queuedStartedAtUtc = "2026-08-13T00:00:01.0000000Z",
                queuedDurationMs = 2000
            };
            var snapshot = HeartbeatSnapshotContract.BuildBusyKeepAliveSnapshot(
                cached,
                activity,
                updatedAtUtc);
            var store = CreateStore(updatedAtUtc);

            Assert.That(store.PublishStatus(snapshot), Is.True);
            var status = JsonConvert.DeserializeObject<StatusContract>(File.ReadAllText(_statusPath));
            Assert.That(status.ready, Is.False);
            Assert.That(status.busy, Is.True);
            Assert.That(status.busyCommand, Is.EqualTo("test.run"));
            Assert.That(status.busyStale, Is.True);
            Assert.That(status.queuedCommands, Is.EqualTo(1));
            Assert.That(status.updatedAtUtc, Is.EqualTo(updatedAtUtc));
            Assert.That(status.lastUpdatedUtc, Is.EqualTo(updatedAtUtc));
            Assert.That(status.connection.ready, Is.False);
            Assert.That(status.connection.busy, Is.True);
            Assert.That(status.connection.busyRequestId, Is.EqualTo("request-123"));
            Assert.That(status.connection.busyDurationMs, Is.EqualTo(31000));
            Assert.That(status.connection.busyStale, Is.True);
            Assert.That(status.connection.busyStaleAfterMs, Is.EqualTo(30000));
            Assert.That(status.connection.queuedCommands, Is.EqualTo(1));
            Assert.That(status.message, Does.Contain("running longer than expected"));
        }

        private HeartbeatPublicationStore CreateStore(
            string updatedAtUtc,
            Func<string, string, Encoding, bool> atomicWrite = null)
        {
            return new HeartbeatPublicationStore(
                _statusPath,
                _instancePath,
                new UTF8Encoding(false),
                () => updatedAtUtc,
                atomicWrite);
        }

        private static object CreateIdentity(int port)
        {
            return new
            {
                projectName = "TestProject",
                projectPath = "C:/TestProject",
                projectHash = "abc123",
                port,
                url = "http://127.0.0.1:" + port,
                pid = 1234,
                statusFile = "C:/status/abc123.json"
            };
        }
    }
}
