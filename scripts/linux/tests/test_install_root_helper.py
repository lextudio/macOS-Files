# Copyright (c) Files Community. Licensed under the MIT License.
"""Non-root staging tests. Never invoke sudo, pkexec, or the installed helper."""
from contextlib import redirect_stderr
import hashlib
import io
import importlib.util
import os
from pathlib import Path
import stat
import struct
import subprocess
import tempfile
import unittest
from unittest import mock
import xml.etree.ElementTree as ET

INSTALLER = Path(__file__).resolve().parents[1] / "install-root-helper.py"
spec = importlib.util.spec_from_file_location("root_helper_installer", INSTALLER)
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)


class InstallerTests(unittest.TestCase):
    def setUp(self):
        self.assertNotEqual(os.geteuid(), 0, "Tests must run without root privileges")
        self.temporary = tempfile.TemporaryDirectory(prefix="files-helper-installer-test-")
        self.directory = Path(self.temporary.name)
        self.source = self.directory / "build"
        self.source.mkdir()
        self.helper = self.source / "files-elevation-helper"
        header = bytearray(64)
        header[:7] = b"\x7fELF\x02\x01\x01"
        struct.pack_into("<HH", header, 16, 3, 62)
        self.contents = bytes(header) + b"fixture, never executed"
        self.helper.write_bytes(self.contents)
        self.digest = hashlib.sha256(self.contents).hexdigest()
        self.stage = self.directory / "stage"

    def tearDown(self):
        self.temporary.cleanup()

    def arguments(self):
        return ["--from", str(self.source), "--sha256", self.digest, "--destdir", str(self.stage)]

    def test_staging_uses_embedded_prompt_policy_and_private_verified_snapshot(self):
        run = subprocess.run
        snapshots = []

        def install(command, **kwargs):
            snapshot = Path(command[-2])
            snapshots.append(snapshot.parent)
            self.assertEqual(os.geteuid(), snapshot.parent.stat().st_uid)
            self.assertEqual(0o700, stat.S_IMODE(snapshot.parent.stat().st_mode))
            self.assertNotEqual(self.source, snapshot.parent)
            self.helper.write_bytes(b"attacker changed the source after verification")
            return run(command, **kwargs)

        with mock.patch.object(installer.subprocess, "run", side_effect=install):
            installer.main(self.arguments())
        output = self.stage / "usr/lib/linuxfiles/files-elevation-helper"
        self.assertEqual(self.contents, output.read_bytes())
        self.assertEqual(0o755, stat.S_IMODE(output.stat().st_mode))
        policy = self.stage / "usr/share/polkit-1/actions/io.github.memergamer.LinuxFiles.root-actions.policy"
        self.assertEqual(installer.POLICY, policy.read_text())
        self.assertEqual(0o644, stat.S_IMODE(policy.stat().st_mode))
        self.assertTrue(all(not path.exists() for path in snapshots))

    def test_embedded_policy_matches_package_and_has_bound_auth_admin_prompt(self):
        package = INSTALLER.parents[2] / "packaging/linux/io.github.memergamer.LinuxFiles.root-actions.policy"
        self.assertEqual(package.read_text(), installer.POLICY)
        action = ET.fromstring(installer.POLICY).find("action")
        self.assertIn("$(command_line)", action.findtext("message"))
        self.assertIn("$(program)", action.findtext("message"))
        self.assertIn("root file access", action.findtext("message"))
        self.assertEqual("auth_admin", action.findtext("defaults/allow_active"))
        self.assertEqual("/usr/lib/linuxfiles/files-elevation-helper", action.find("annotate").text)

    def test_live_install_refuses_each_package_manager_before_snapshot_or_install(self):
        paths = ("/usr/lib/linuxfiles", installer.HELPER_PATH, installer.POLICY_PATH)
        for tool in ("/usr/bin/pacman", "/usr/bin/dpkg-query", "/usr/bin/rpm"):
            for owned_path in paths:
                with self.subTest(tool=tool, path=owned_path):
                    def query(command, **kwargs):
                        self.assertEqual(tool, command[0])
                        return subprocess.CompletedProcess(command, 0 if command[-1] == owned_path else 1)

                    arguments = self.arguments()
                    arguments[-1] = "/"
                    with mock.patch.object(installer.os, "geteuid", return_value=0), \
                            mock.patch.object(installer.os, "access", side_effect=lambda path, mode: path == tool), \
                            mock.patch.object(installer.subprocess, "run", side_effect=query), \
                            mock.patch.object(installer, "snapshot_helper") as snapshot:
                        diagnostic = io.StringIO()
                        with redirect_stderr(diagnostic), self.assertRaises(SystemExit):
                            installer.main(arguments)
                        self.assertIn(f"{owned_path} is owned by a distro package", diagnostic.getvalue())
                        self.assertIn("omit --install-root-helper", diagnostic.getvalue())
                        snapshot.assert_not_called()
                        self.assertEqual(self.contents, self.helper.read_bytes())
                        self.assertFalse(self.stage.exists())

    def test_local_install_delegates_to_package_ownership_guard(self):
        local = INSTALLER.with_name("install-local.sh").read_text()
        self.assertIn('/usr/bin/sudo /usr/bin/python3 "$root/scripts/linux/install-root-helper.py"', local)
        self.assertNotIn("/usr/lib/linuxfiles", local)
        self.assertNotIn("/usr/share/polkit-1/actions", local)

    def test_unowned_paths_allow_tarball_helper_installation(self):
        with mock.patch.object(installer.os, "access", return_value=True), \
                mock.patch.object(installer.subprocess, "run", return_value=subprocess.CompletedProcess([], 1)) as run:
            installer.refuse_package_owned_paths()
        self.assertEqual(9, run.call_count)
        for call in run.call_args_list:
            self.assertNotIn("shell", call.kwargs)
            self.assertEqual(subprocess.DEVNULL, call.kwargs["stdout"])

    def test_package_query_errors_refuse_installation(self):
        with mock.patch.object(installer.os, "access", return_value=True), \
                mock.patch.object(installer.subprocess, "run", return_value=subprocess.CompletedProcess([], 2)):
            with self.assertRaisesRegex(ValueError, "cannot check package ownership"):
                installer.refuse_package_owned_paths()

    def test_aot_helper_uses_invariant_globalization_and_excludes_symlink_creation(self):
        repo = INSTALLER.parents[2]
        project = ET.parse(repo / "src/Files.Platform.Linux.ElevationHelper/Files.Platform.Linux.ElevationHelper.csproj")
        self.assertEqual("true", project.findtext("PropertyGroup/InvariantGlobalization"))
        native = (repo / "src/Files.Platform.Posix/Native/PosixNative.cs").read_text(encoding="utf-8-sig")
        helper_branch = native.split("#if ELEVATION_HELPER", 1)[1].split("#endif", 1)[0]
        self.assertNotIn("symlinkat", helper_branch)
        public_branch = native.split("#if !ELEVATION_HELPER", 1)[1].split("#endif", 1)[0]
        self.assertIn("SymlinkAt", public_branch)

    def test_digest_mismatch_never_installs(self):
        self.helper.write_bytes(self.contents + b"tampered")
        with mock.patch.object(installer.subprocess, "run") as run:
            with self.assertRaises(SystemExit):
                installer.main(self.arguments())
            run.assert_not_called()

    def test_non_elf_with_matching_digest_is_refused(self):
        self.helper.write_bytes(b"#!/bin/sh\nexit 0\n")
        self.digest = hashlib.sha256(self.helper.read_bytes()).hexdigest()
        with self.assertRaises(SystemExit):
            installer.main(self.arguments())
        self.assertFalse(self.stage.exists())

    def test_symlink_source_is_refused(self):
        actual = self.source / "actual"
        self.helper.rename(actual)
        self.helper.symlink_to(actual)
        with self.assertRaises(SystemExit):
            installer.main(self.arguments())
        self.assertFalse(self.stage.exists())

    def test_destdir_alias_of_root_still_requires_root(self):
        alias = self.directory / "root-alias"
        alias.symlink_to("/", target_is_directory=True)
        arguments = self.arguments()
        arguments[-1] = str(alias)
        with mock.patch.object(installer.subprocess, "run") as run:
            with self.assertRaises(SystemExit):
                installer.main(arguments)
            run.assert_not_called()

    def test_oversized_snapshot_is_refused_and_cleaned(self):
        with mock.patch.object(installer, "MAX_HELPER_BYTES", 1):
            with self.assertRaises(SystemExit):
                installer.main(self.arguments())
        self.assertFalse(self.stage.exists())


if __name__ == "__main__":
    unittest.main()
