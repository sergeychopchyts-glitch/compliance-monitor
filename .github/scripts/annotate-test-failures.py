"""Turn failed tests in `dotnet test` output into GitHub error annotations.

Job logs need a GitHub login to read; annotations are public, so anyone can see why CI failed.
Usage: python3 annotate-test-failures.py test-output.txt
"""
import re
import sys

MAX_ANNOTATIONS = 10   # GitHub shows at most 10 error annotations per step
MAX_DETAIL_LINES = 15


def escape(text: str) -> str:
    # Workflow-command escaping: https://docs.github.com/actions/reference/workflow-commands-for-github-actions
    return text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def escape_property(text: str) -> str:
    # Property values (title=...) also need ":" and "," escaped.
    return escape(text).replace(":", "%3A").replace(",", "%2C")


def failures(lines):
    current = None
    for line in lines:
        # "failed <test name, which may contain spaces> (12ms)"
        start = re.match(r"^failed (.+?)(?: \(\d[^)]*\))?$", line)
        if start:
            if current:
                yield current
            current = (start.group(1), [])
        elif current is not None:
            if not line.strip() or re.match(r"^\S", line):
                yield current
                current = None
            elif len(current[1]) < MAX_DETAIL_LINES and not line.strip().startswith("from "):
                current[1].append(line.strip())
    if current:
        yield current


def main(path: str) -> None:
    try:
        lines = open(path, encoding="utf-8", errors="replace").read().splitlines()
    except FileNotFoundError:
        print(f"::error::{escape('No test output found: the failure happened before the Test step.')}")
        return
    found = list(failures(lines))
    for name, detail in found[:MAX_ANNOTATIONS]:
        print(f"::error title={escape_property(name)}::{escape(chr(10).join(detail) or 'Test failed (no message).')}")
    if len(found) > MAX_ANNOTATIONS:
        print(f"::error::{escape(f'{len(found) - MAX_ANNOTATIONS} more failed tests; see the job log.')}")
    if not found:
        print(f"::error::{escape('dotnet test failed but no failed test was found in its output; see the job log.')}")


if __name__ == "__main__":
    main(sys.argv[1])
