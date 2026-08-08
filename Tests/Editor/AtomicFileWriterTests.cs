using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace CubicEngine.UnityCli.Tests
{
    internal sealed class AtomicFileWriterTests
    {
        private string _directory;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "cubic-cli-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
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
        public void TryWriteAllTextCreatesAndAtomicallyReplacesDestination()
        {
            var path = Path.Combine(_directory, "status.json");
            var encoding = new UTF8Encoding(false);

            Assert.That(AtomicFileWriter.TryWriteAllText(path, "{\"generation\":1}", encoding), Is.True);
            Assert.That(File.ReadAllText(path), Is.EqualTo("{\"generation\":1}"));

            Assert.That(AtomicFileWriter.TryWriteAllText(path, "{\"generation\":2}", encoding), Is.True);
            Assert.That(File.ReadAllText(path), Is.EqualTo("{\"generation\":2}"));
            Assert.That(Directory.GetFiles(_directory).Select(Path.GetFileName),
                Is.EquivalentTo(new[] { "status.json" }));
        }

        [Test]
        public void TryWriteAllTextLeavesExistingDestinationIntactWhenReplacementIsLocked()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                Assert.Ignore("Windows sharing behavior is required for this test.");
            }

            var path = Path.Combine(_directory, "status.json");
            File.WriteAllText(path, "old");

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.That(AtomicFileWriter.TryWriteAllText(path, "new", new UTF8Encoding(false)), Is.False);
            }

            Assert.That(File.ReadAllText(path), Is.EqualTo("old"));
            Assert.That(Directory.GetFiles(_directory).Select(Path.GetFileName),
                Is.EquivalentTo(new[] { "status.json" }));
        }
    }
}
