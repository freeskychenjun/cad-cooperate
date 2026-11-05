#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""check_py39_syntax.py -- guard the "Python 3.9+" claim without needing a 3.9 interpreter.

The CI runner installs one CPython (3.11+) and the vendored tools/py-libs tree is gitignored, so the
only practical way to keep the 3.9 floor honest is a *grammar* gate: compile every tracked .py of the
project with `ast.parse(..., feature_version=(3, 9))`, which rejects match/case, `except*`, parenthesized
context managers, PEP 604 `X | Y` in annotations under 3.9's grammar, etc.

Exit codes: 0 = every file parses under the 3.9 grammar, 1 = at least one violation.
Usage: python tests/check_py39_syntax.py [--verbose]
"""
from __future__ import annotations

import ast
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCAN_DIRS = ("scripts", "tests")
SKIP_DIRS = {"py-libs", "__pycache__", "node_modules", ".git", "tmp"}


def iter_py(root):
    for base, dirs, files in os.walk(root):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for name in sorted(files):
            if name.endswith(".py"):
                yield os.path.join(base, name)


def main(argv=None):
    verbose = "--verbose" in (argv or sys.argv[1:])
    files = []
    for d in SCAN_DIRS:
        p = os.path.join(ROOT, d)
        if os.path.isdir(p):
            files.extend(iter_py(p))
    if not files:
        print("FAIL no python files found under %s" % (SCAN_DIRS,))
        return 1

    bad = 0
    for path in files:
        rel = os.path.relpath(path, ROOT)
        try:
            with open(path, "r", encoding="utf-8") as fh:
                src = fh.read()
        except OSError as exc:
            print("FAIL %s : cannot read (%s)" % (rel, exc))
            bad += 1
            continue
        try:
            ast.parse(src, filename=rel, feature_version=(3, 9))
        except SyntaxError as exc:
            print("FAIL %s:%s : not valid Python 3.9 grammar: %s" % (rel, exc.lineno, exc.msg))
            bad += 1
            continue
        if verbose:
            print("PASS %s" % rel)

    total = len(files)
    if bad:
        print("[py39-syntax] RESULT: FAIL (%d/%d files violate the 3.9 grammar)" % (bad, total))
        return 1
    print("[py39-syntax] RESULT: PASS (%d files parse under the Python 3.9 grammar)" % total)
    return 0


if __name__ == "__main__":
    sys.exit(main())
