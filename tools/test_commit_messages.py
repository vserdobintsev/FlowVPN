import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from check_commit_messages import cyrillic_lines, valid_title


class CommitMessagesTests(unittest.TestCase):
    def test_valid_conventional_titles(self):
        for title in ("fix(core): pin bundled executable", "docs: clarify installation", "feat(api)!: change the contract"):
            with self.subTest(title=title):
                self.assertTrue(valid_title(title))

    def test_invalid_titles(self):
        for title in ("Исправить запуск", "fix: исправить запуск", "Fix: launch", "fix: ", "fix: " + "a" * 116, "fix: launch\nextra"):
            with self.subTest(title=title):
                self.assertFalse(valid_title(title))

    def test_english_message_with_body_and_breaking_change(self):
        self.assertEqual([], cyrillic_lines("fix(core): pin executable\n\nKeep data separate.\n\nBREAKING CHANGE: --root affects data only."))

    def test_russian_body_is_rejected_even_with_english_subject(self):
        self.assertEqual([3], cyrillic_lines("fix(core): pin executable\n\nИзолировать каталог данных."))

    def test_cyrillic_subject_is_rejected(self):
        self.assertEqual([1], cyrillic_lines("fix: исправить запуск"))

    def test_coauthor_identity_can_use_native_name(self):
        self.assertEqual([], cyrillic_lines("fix: pin executable\n\nCo-authored-by: Иван Петров <ivan@example.test>"))

    def test_coauthor_exception_does_not_hide_subject_or_prose(self):
        self.assertEqual([1, 3, 4], cyrillic_lines("Co-authored-by: Иван <ivan@example.test>\n\nCo-authored-by: Иван без адреса\nCo-authored-by: Иван <ivan@example.test> и описание"))

    def test_other_identity_or_metadata_does_not_hide_body(self):
        self.assertEqual([3], cyrillic_lines("fix: pin executable\n\nReviewed-by: русский текст"))


class GitRangeTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="flowvpn-commit-check-")
        self.addCleanup(self.directory.cleanup)
        self.git("-c", "init.templateDir=", "init", "--quiet")
        self.git("config", "user.name", "Commit Check Fixture")
        self.git("config", "user.email", "fixture@example.test")
        self.tree = self.git("hash-object", "-t", "tree", "-w", "--stdin", text="").strip()

    def git(self, *args, text=None):
        return subprocess.run(["git", *args], cwd=self.directory.name, input=text, text=True, encoding="utf-8", check=True, capture_output=True).stdout

    def commit(self, message, parent=None):
        args = ["-c", "commit.gpgsign=false", "commit-tree", self.tree]
        if parent:
            args.extend(["-p", parent])
        return self.git(*args, text=message).strip()

    def check_range(self, base, head):
        script = Path(__file__).with_name("check_commit_messages.py").resolve()
        env = os.environ.copy()
        env["PR_TITLE"] = "ci: enforce commit message language"
        env["PYTHONUTF8"] = "1"
        return subprocess.run([sys.executable, str(script), "--base", base, "--head", head], cwd=self.directory.name, env=env, text=True, encoding="utf-8", capture_output=True)

    def test_existing_russian_history_is_not_rechecked(self):
        base = self.commit("docs: earlier change\n\nСохранённое описание.")
        head = self.commit("fix: keep new prose in English\n\nA new explanation.", base)
        result = self.check_range(base, head)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_new_russian_body_fails_the_real_git_range(self):
        base = self.commit("docs: initial state")
        head = self.commit("fix: new change\n\nНовое русское описание.", base)
        result = self.check_range(base, head)
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertIn(head[:12], result.stdout)

    def test_empty_range_is_valid(self):
        base = self.commit("docs: earlier change\n\nПрежнее описание.")
        result = self.check_range(base, base)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
