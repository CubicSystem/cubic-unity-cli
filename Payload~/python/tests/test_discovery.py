import json
import os
import sys
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from cubic_cli import discovery, main


class DiscoveryTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary_directory.cleanup)
        self.root = Path(self.temporary_directory.name)
        self.project = self.root / "UnityProject"
        self.project.mkdir()
        self.instances = self.root / "instances"
        self.instances.mkdir()

    def instance_payload(self, **overrides):
        payload = {
            "projectName": self.project.name,
            "projectPath": str(self.project),
            "projectHash": "abc123",
            "port": 48061,
            "url": "http://127.0.0.1:48061",
            "updatedAtUtc": datetime.now(timezone.utc).isoformat(),
        }
        payload.update(overrides)
        return payload

    def write_instance(self, payload=None) -> Path:
        path = self.instances / "abc123.json"
        path.write_text(json.dumps(payload or self.instance_payload()), encoding="utf-8")
        return path

    def test_load_instances_retries_a_truncated_json_file(self):
        path = self.instances / "abc123.json"
        path.write_text('{"projectPath":', encoding="utf-8")
        complete_payload = self.instance_payload()

        def finish_write(_delay):
            path.write_text(json.dumps(complete_payload), encoding="utf-8")

        with mock.patch.object(discovery, "instances_root", return_value=self.instances), mock.patch.object(
            discovery.time, "sleep", side_effect=finish_write
        ) as sleep:
            instances = discovery.load_instances()

        self.assertEqual(1, sleep.call_count)
        self.assertEqual(1, len(instances))
        self.assertEqual(48061, instances[0].port)

    def test_load_instances_retries_an_empty_json_file(self):
        path = self.instances / "abc123.json"
        path.write_bytes(b"")
        complete_payload = self.instance_payload()

        def finish_write(_delay):
            path.write_text(json.dumps(complete_payload), encoding="utf-8")

        with mock.patch.object(discovery, "instances_root", return_value=self.instances), mock.patch.object(
            discovery.time, "sleep", side_effect=finish_write
        ):
            instances = discovery.load_instances()

        self.assertEqual(1, len(instances))

    def test_stable_reader_retries_transient_io_failure(self):
        expected = (self.instance_payload(), datetime.now(timezone.utc))
        with mock.patch.object(
            discovery,
            "_read_json_once",
            side_effect=[PermissionError("sharing violation"), expected],
        ), mock.patch.object(discovery.time, "sleep") as sleep:
            loaded = discovery.read_stable_json(self.instances / "abc123.json")

        self.assertEqual(expected, loaded)
        sleep.assert_called_once()

    def test_instance_timestamp_falls_back_to_file_mtime(self):
        path = self.write_instance(self.instance_payload(updatedAtUtc="not-a-timestamp"))
        expected = datetime.now(timezone.utc) - timedelta(seconds=4)
        os.utime(path, (expected.timestamp(), expected.timestamp()))

        with mock.patch.object(discovery, "instances_root", return_value=self.instances):
            instance = discovery.load_instances()[0]

        self.assertAlmostEqual(expected.timestamp(), instance.updated_at.timestamp(), delta=1.0)
        self.assertTrue(discovery.is_active_instance(instance, max_age_seconds=10.0))

    def test_reloading_status_uses_its_bounded_freshness_window(self):
        status_path = self.root / "status.json"
        reloading_at = datetime.now(timezone.utc) - timedelta(seconds=60)
        status_path.write_text(
            json.dumps({"reloading": True, "updatedAtUtc": reloading_at.isoformat()}),
            encoding="utf-8",
        )
        instance = discovery.InstanceInfo(
            project_name=self.project.name,
            project_path=str(self.project),
            project_hash="abc123",
            port=48061,
            url="http://127.0.0.1:48061",
            updated_at_utc=reloading_at.isoformat(),
            status_file=str(status_path),
        )

        self.assertFalse(discovery.is_active_instance(instance, max_age_seconds=15.0))
        self.assertTrue(discovery.is_reloading_instance(instance, max_age_seconds=180.0))
        self.assertFalse(discovery.is_reloading_instance(instance, max_age_seconds=30.0))

    def test_status_updated_at_prefers_canonical_field_and_supports_legacy_field(self):
        current = datetime.now(timezone.utc)
        legacy = current - timedelta(days=1)

        parsed = discovery.parse_status_updated_at(
            {
                "updatedAtUtc": current.isoformat(),
                "lastUpdatedUtc": legacy.isoformat(),
            }
        )
        legacy_parsed = discovery.parse_status_updated_at({"lastUpdatedUtc": legacy.isoformat()})

        self.assertEqual(current, parsed)
        self.assertEqual(legacy, legacy_parsed)

    def test_truncated_status_does_not_discard_a_fresh_instance_heartbeat(self):
        status_path = self.root / "status.json"
        status_path.write_text('{"ready":', encoding="utf-8")
        self.write_instance(self.instance_payload(statusFile=str(status_path)))

        with mock.patch.object(discovery, "instances_root", return_value=self.instances), mock.patch.object(
            discovery.time, "sleep"
        ):
            resolved = discovery.resolve_instance(
                cwd=str(self.project),
                project=str(self.project),
                max_age_seconds=15.0,
            )

        self.assertEqual(48061, resolved.port)

    def test_recent_status_fallback_uses_stable_reader_and_file_mtime(self):
        status_path = self.root / "status.json"
        status_path.write_text('{"ready":', encoding="utf-8")
        instance = discovery.InstanceInfo(
            project_name=self.project.name,
            project_path=str(self.project),
            project_hash="abc123",
            port=48061,
            url="http://127.0.0.1:48061",
            updated_at_utc=None,
            status_file=str(status_path),
        )

        def finish_write(_delay):
            status_path.write_text(json.dumps({"ready": True}), encoding="utf-8")

        with mock.patch.object(discovery.time, "sleep", side_effect=finish_write):
            snapshot = main.load_recent_status_snapshot(instance)

        self.assertEqual({"ready": True}, snapshot)


if __name__ == "__main__":
    unittest.main()
