"""Check future commit prose without imposing a language detector on identities."""

import argparse
import os
import re
import subprocess
import sys


TITLE = re.compile(
    r"(?:feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert)"
    r"(?:\([a-z0-9][a-z0-9._/-]*\))?!?: [A-Za-z0-9][\x20-\x7E]*"
)
CYRILLIC = re.compile(r"[\u0400-\u052f\u1c80-\u1c8f\u2de0-\u2dff\ua640-\ua69f\U0001e030-\U0001e08f]")
COAUTHOR = re.compile(r"Co-authored-by: [^<>\r\n]+ <[^<>\s@]+@[^<>\s@]+>", re.IGNORECASE)


def valid_title(title):
    return len(title) <= 120 and TITLE.fullmatch(title) is not None


def cyrillic_lines(message):
    """Names in complete coauthor trailers are identities, not commit prose."""
    return [
        number
        for number, line in enumerate(message.splitlines(), 1)
        if CYRILLIC.search(line) and not (number > 1 and COAUTHOR.fullmatch(line))
    ]


def git(*args):
    return subprocess.run(
        ["git", *args], check=True, capture_output=True, encoding="utf-8"
    ).stdout


def resolve_commit(revision):
    commit = git("rev-parse", "--verify", "--end-of-options", revision + "^{commit}").strip()
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Expected a full Git commit SHA.")
    return commit


def introduced_messages(base, head):
    # The range excludes existing main history, including messages retained as-is.
    records = git("-c", "i18n.logOutputEncoding=utf-8", "log", "--format=%H%x00%B%x00", f"{resolve_commit(base)}..{resolve_commit(head)}").split("\0")
    if records[-1].strip() or len(records) % 2 != 1:
        raise ValueError("Unexpected Git log output.")
    return [(records[index].strip(), records[index + 1]) for index in range(0, len(records) - 1, 2)]


def main():
    parser = argparse.ArgumentParser(description="Check new commit messages; optional PR_TITLE comes from the environment.")
    parser.add_argument("--base", required=True)
    parser.add_argument("--head", default="HEAD")
    args = parser.parse_args()
    title = os.environ.get("PR_TITLE")
    failed = False
    if title is not None and not valid_title(title):
        print("::error::Заголовок PR должен быть английским Conventional Commit, не длиннее 120 символов.")
        failed = True
    messages = introduced_messages(args.base, args.head)
    for commit, message in messages:
        lines = cyrillic_lines(message)
        if lines:
            print(f"::error::Коммит {commit[:12]}: кириллица в строках {', '.join(map(str, lines))}. Заголовок и тело сообщения пишутся на английском; описание PR может быть русским.")
            failed = True
    if not failed:
        print(f"Проверено новых сообщений: {len(messages)}. Кириллица в тексте коммитов не найдена.")
    return int(failed)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, subprocess.CalledProcessError, ValueError) as error:
        print(f"::error::Не удалось проверить сообщения коммитов ({type(error).__name__}).", file=sys.stderr)
        sys.exit(1)
