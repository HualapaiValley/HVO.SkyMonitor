"""Exercise archive maintenance against deterministic stock mc responses."""

import contextlib
import hashlib
import importlib.machinery
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest
from unittest.mock import patch
from urllib.parse import urlsplit

SCRIPT = Path(__file__).resolve().parents[2] / "scripts" / "silo:archive"
LOADER = importlib.machinery.SourceFileLoader("silo_archive", str(SCRIPT))
SPEC = importlib.util.spec_from_loader(LOADER.name, LOADER)
archive = importlib.util.module_from_spec(SPEC)
LOADER.exec_module(archive)

STORE = "silo-archive-" + "1" * 32
BINDING = "b" * 64
PAYLOAD = "p/full/aa/" + "a" * 64
VERSION = "12345678-abcd-1234-abcd-123456789abc"
TARGET = "archive/archive-bucket"
EXPIRY = f"{TARGET}/meta/archive-expiry/" + hashlib.sha256(
    "\n".join((STORE, "archive-bucket", PAYLOAD, VERSION)).encode()
).hexdigest()


class FakeMc:
    def __init__(self):
        self.release = archive.RELEASE
        self.archive_release = archive.RELEASE
        self.deployments = {"source": "source-id", "archive": "archive-id"}
        self.lock = "Enabled"
        self.retention = ""
        self.lifecycle = False
        self.outbound = []
        self.source_rules = []
        self.objects = {}
        self.payload_exists = True
        self.interrupt_after_delete = False
        self.calls = []

    def __call__(self, command, *, env, input=None, **kwargs):
        args = tuple(command[1:])
        self.calls.append((args, env))
        if args == ("--version",):
            return subprocess.CompletedProcess(command, 0,
                f"mc version RELEASE.{self.release.replace(':', '-')} (Silo object storage client)")
        assert args[0] == "--json"
        args = args[1:]

        def result(code=None, **row):
            if code:
                row = {"status": "error", "error": {"cause": {"error": {"Code": code}}}}
            return subprocess.CompletedProcess(command, int(code is not None), json.dumps(row).encode())

        if args[:2] == ("admin", "info"):
            server = args[2]
            release = self.archive_release if server == "archive" else archive.RELEASE
            return result(info={"servers": [{"version": release}], "mode": "online",
                            "deploymentID": self.deployments[server]})
        if args[:2] == ("version", "info"):
            return result(versioning={"status": "Enabled"})
        if args[:2] == ("retention", "info"):
            return result(enabled=self.lock, mode=self.retention)
        if args[:3] == ("ilm", "rule", "export"):
            return result() if self.lifecycle else result("NoSuchLifecycleConfiguration")
        if args[:2] == ("replicate", "export"):
            return result(config={"Rules": self.outbound if args[2] == TARGET else self.source_rules})
        if args[0] == "cat":
            if args[1] == "source/source-bucket/meta/instance.v1.json":
                return result(format="hvo.cameraagent.silo.instance/1", storageBindingId=BINDING, bucket="source-bucket")
            return result(**self.objects[args[1]])
        if args[0] == "stat":
            exists = self.payload_exists if args[1] == "--version-id" else args[1] in self.objects
            return result() if exists else result("NoSuchVersion")
        if args[0] == "ls":
            return result(key="previously-present") if self.objects else subprocess.CompletedProcess(command, 0, b"")
        if args[:3] == ("--custom-header", "If-None-Match:*", "pipe"):
            assert args[3] not in self.objects
            self.objects[args[3]] = json.loads(input)
            return result()
        if args == ("rm", "--version-id", VERSION, f"{TARGET}/{PAYLOAD}"):
            self.payload_exists = False
            if self.interrupt_after_delete:
                raise subprocess.TimeoutExpired(command, 30)
            return result()
        raise AssertionError(f"Unexpected fake mc arguments {args}")


class SiloArchiveScriptTests(unittest.TestCase):
    def setUp(self):
        self.mc = FakeMc()
        self.environment = {
            "MC_HOST_source": "http://sourceKey:sourceSecret@source.example:9000",
            "MC_HOST_archive": "https://adminKey:adminSecret@archive.example:9000",
            "MC_HOST_replica": "https://replicaKey:replicaSecret@ARCHIVE.example:9000",
            "MC_HOST_reader": "https://readerKey:readerSecret@archive.example:9000",
        }

    def run_script(self, operation="enable", *extra):
        args = [str(SCRIPT), operation, "--source-bucket", "source-bucket", "--archive-bucket", "archive-bucket",
                "--store-id", STORE, "--binding", BINDING, *extra]
        with patch.dict(os.environ, self.environment, clear=True), patch.object(sys, "argv", args), (
            patch.object(archive.subprocess, "run", side_effect=self.mc)
        ), contextlib.redirect_stdout(io.StringIO()) as output:
            archive.main()
        return json.loads(output.getvalue())

    def test_cleartext_archive_is_refused_before_contact(self):
        self.environment["MC_HOST_archive"] = self.environment["MC_HOST_archive"].replace("https:", "http:")
        with self.assertRaisesRegex(archive.Refused, "requires HTTPS"):
            self.run_script()
        self.assertEqual([], self.mc.calls)

    def test_same_deployment_is_refused(self):
        self.mc.deployments["archive"] = self.mc.deployments["source"]
        with self.assertRaisesRegex(archive.Refused, "different Silo deployment"):
            self.run_script()

    def test_missing_object_lock_is_refused(self):
        self.mc.lock = "Disabled"
        with self.assertRaisesRegex(archive.Refused, "object lock enabled"):
            self.run_script()

    def test_bucket_default_retention_is_refused(self):
        self.mc.retention = "COMPLIANCE"
        with self.assertRaisesRegex(archive.Refused, "no bucket default hold period"):
            self.run_script()

    def test_archive_lifecycle_is_refused(self):
        self.mc.lifecycle = True
        with self.assertRaisesRegex(archive.Refused, "Remove archive lifecycle"):
            self.run_script()

    def test_archive_outbound_rules_are_refused(self):
        self.mc.outbound = [{"ID": "outbound"}]
        with self.assertRaisesRegex(archive.Refused, "outbound replication"):
            self.run_script()

    def test_unmanaged_source_rule_is_refused(self):
        self.mc.source_rules = [{"ID": "other"}]
        with self.assertRaisesRegex(archive.Refused, "unmanaged replication"):
            self.run_script()

    def test_first_enable_refuses_nonempty_archive(self):
        self.mc.objects[f"{TARGET}/other-key"] = {}
        with self.assertRaisesRegex(archive.Refused, "Only an empty archive bucket"):
            self.run_script()
        self.assertNotIn(f"{TARGET}/{archive.MARKER}", self.mc.objects)

    def install_marker(self):
        self.mc.objects[f"{TARGET}/{archive.MARKER}"] = {
            "format": "hvo.cameraagent.silo.archive/1",
            "storeId": STORE,
            "bucket": "archive-bucket",
            "sourceBindingId": BINDING,
            "retentionPolicy": archive.RETENTION,
        }

    def test_expiry_resumes_after_delete_lands_and_the_command_is_interrupted(self):
        self.install_marker()
        self.mc.interrupt_after_delete = True
        arguments = ("expire", "--key", PAYLOAD, "--version-id", VERSION)
        with self.assertRaises(subprocess.TimeoutExpired):
            self.run_script(*arguments)
        self.assertFalse(self.mc.payload_exists)
        self.assertEqual("authorized", self.mc.objects[EXPIRY + ".intent.json"]["disposition"])
        self.assertNotIn(EXPIRY + ".json", self.mc.objects)
        self.assertEqual("policy-expired", self.run_script(*arguments)["disposition"])
        self.assertEqual(self.mc.objects[EXPIRY + ".json"], self.run_script(*arguments))
        self.assertEqual(1, sum("rm" in args for args, _ in self.mc.calls))

    def test_expiry_checks_qualified_client_and_archive_before_writing(self):
        self.install_marker()
        arguments = ("expire", "--key", PAYLOAD, "--version-id", VERSION)
        self.mc.release = "unsupported"
        with self.assertRaisesRegex(archive.Refused, "qualified stock Silo image"):
            self.run_script(*arguments)
        self.mc.release = archive.RELEASE
        self.mc.archive_release = "unsupported"
        with self.assertRaisesRegex(archive.Refused, "archive server must run the qualified"):
            self.run_script(*arguments)
        self.assertTrue(self.mc.payload_exists)
        self.assertEqual(1, len(self.mc.objects))

    def test_every_mc_invocation_strips_proxy_environment(self):
        proxies = ("HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY")
        self.environment.update({key: "http://invalid.example" for name in proxies for key in (name, name.lower())})
        self.mc.lock = "Disabled"
        with self.assertRaises(archive.Refused):
            self.run_script()
        self.assertGreater(len(self.mc.calls), 1)
        for _, environment in self.mc.calls:
            self.assertFalse(set(proxies) & {key.upper() for key in environment})
            self.assertEqual(self.environment["MC_HOST_archive"], environment["MC_HOST_archive"])

    def test_replica_cannot_modify_tags_on_custody_metadata(self):
        statements = archive.policy("archive-bucket", True)["Statement"]
        actual = [row for row in statements if row["Effect"] == "Deny" and "s3:ReplicateTags" in row["Action"]]
        self.assertEqual(1, len(actual))
        self.assertEqual({"arn:aws:s3:::archive-bucket/meta/archive.v1.json",
                          "arn:aws:s3:::archive-bucket/meta/archive-expiry/*"}, set(actual[0]["Resource"]))

    def test_target_readback_normalizes_hostname_case(self):
        remote = {"arn": "arn:fixture", "sourcebucket": "source-bucket", "targetbucket": "archive-bucket",
                  "credentials": {"accessKey": "replicaKey"}, "endpoint": "archive.example:9000", "secure": True,
                  "path": "on", "disableProxy": True, "replicationSync": False}
        args = type("Args", (), {"source_bucket": "source-bucket", "archive_bucket": "archive-bucket"})()
        endpoint = urlsplit(self.environment["MC_HOST_replica"])
        with patch.object(archive, "one", return_value={"remoteTargets": [remote]}):
            self.assertTrue(archive.target_matches({"Destination": {"Bucket": "arn:fixture"}}, "source", endpoint, args))
            remote["endpoint"] = "other.example:9000"
            self.assertFalse(archive.target_matches({"Destination": {"Bucket": "arn:fixture"}}, "source", endpoint, args))


if __name__ == "__main__":
    unittest.main()
