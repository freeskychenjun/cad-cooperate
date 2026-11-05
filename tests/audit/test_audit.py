#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""test_audit.py - offline regression tests for the drawing audit layer.

No CAD, no COM: everything runs against the JSON fixtures in tests/audit/fixtures/
(shape-identical to real `zwc.ps1 info/read` output) plus, when present, the archived
real-CAD snapshots in tests/audit/real/.

    python tests/audit/test_audit.py            # run everything
    python tests/audit/test_audit.py -v         # show every check
    python tests/audit/test_audit.py --regen    # regenerate the JSON fixtures first

Exit code 0 = all PASS, 1 = at least one FAIL.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
AUDIT = os.path.join(REPO, "scripts", "audit", "audit.py")
FIXTURES = os.path.join(HERE, "fixtures")
REAL = os.path.join(HERE, "real")
TMP = tempfile.mkdtemp(prefix="audit-test-")

# CI runners default the console code page to cp1252; some details below are Chinese. Force UTF-8
# with replacement so the run reports PASS/FAIL instead of dying with UnicodeEncodeError.
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

PASS = 0
FAIL = 0
SKIP = 0
FAILURES = []
VERBOSE = "-v" in sys.argv


def check(name, ok, detail=""):
    global PASS, FAIL
    if ok:
        PASS += 1
        if VERBOSE:
            print("PASS  " + name)
    else:
        FAIL += 1
        FAILURES.append((name, detail))
        print("FAIL  " + name + ("  :: " + str(detail) if detail else ""))
    return ok


def skip(name, why):
    global SKIP
    SKIP += 1
    print("SKIP  " + name + "  (" + why + ")")


def fixture(name):
    return os.path.join(FIXTURES, name)


def real(name):
    return os.path.join(REAL, name)


def run(args, expect_exit=None):
    proc = subprocess.run([sys.executable, AUDIT] + args,
                          capture_output=True, text=True, encoding="utf-8", errors="replace")
    if expect_exit is not None:
        check("exit code == %d for %s" % (expect_exit, " ".join(args[:1] + args[-2:])),
              proc.returncode == expect_exit,
              "got %d, stderr=%s" % (proc.returncode, proc.stderr.strip()[:200]))
    return proc


def audit(info_path, read_path, extra=None, expect_exit=None):
    out = os.path.join(TMP, "report-%d.json" % (abs(hash((info_path, read_path))) % 100000))
    args = ["--read", read_path]
    if info_path:
        args += ["--info", info_path]
    args += ["--json", out]
    if extra:
        args += list(extra)
    proc = run(args, expect_exit=expect_exit)
    if not os.path.exists(out):
        return None, proc
    with open(out, encoding="utf-8") as fh:
        return json.load(fh), proc


def rule_counts(report):
    return {rule: info["count"] for rule, info in (report or {}).get("summary", {}).get("byRule", {}).items()}


def skipped_rules(report):
    return {item["rule"]: item["reason"] for item in (report or {}).get("summary", {}).get("skipped", [])}


# --------------------------------------------------------------------------- tests


def test_rule_catalog():
    proc = run(["--list-rules"], expect_exit=0)
    lines = [l for l in proc.stdout.splitlines() if l.strip()]
    check("rule catalog lists 17 rules", len(lines) == 17, "got %d: %s" % (len(lines), lines[:3]))
    ids = [l.split()[0] for l in lines]
    required = ["units_missing", "layer_off_but_used", "layer_empty", "layer_naming",
                "linetype_missing", "linetype_scale", "text_height_outlier", "text_overlap",
                "text_empty", "zero_length_line", "duplicate_entity", "unclosed_polyline",
                "outside_extents", "dim_no_measurement"]
    missing = [r for r in required if r not in ids]
    check("all required rules exist", not missing, missing)
    check("rule ids are unique", len(ids) == len(set(ids)))
    for line in lines:
        parts = line.split()
        check("rule line has severity: " + parts[0], len(parts) >= 3 and parts[1] in ("blocker", "major", "minor"))


def test_problem_rich_offline():
    with open(fixture("expected-hits.json"), encoding="utf-8") as fh:
        expected = json.load(fh)["problem-rich"]
    report, _proc = audit(fixture("problem-rich.info.json"), fixture("problem-rich.read.json"),
                          extra=["--exit-code"], expect_exit=3)
    if not check("problem-rich report generated", report is not None):
        return
    counts = rule_counts(report)
    for rule, minimum in expected["min_hits"].items():
        check("problem-rich hits %s >= %d" % (rule, minimum), counts.get(rule, 0) >= minimum,
              "got %s" % counts.get(rule, 0))
    for rule in expected["must_skip"]:
        check("problem-rich skips %s (not_measured)" % rule,
              skipped_rules(report).get(rule) == "not_measured", skipped_rules(report).get(rule))
    sev = report["summary"]["bySeverity"]
    check("problem-rich blockers >= %d" % expected["blockers_min"],
          sev["blocker"] >= expected["blockers_min"], sev)
    check("problem-rich gate fails", report["gate"]["pass"] is False and report["gate"]["exitCode"] == 3,
          report["gate"])
    # issue record contract
    keys = {"rule", "severity", "handle", "layer", "sample", "suggest"}
    bad = [i for i in report["issues"] if not keys.issubset(i.keys())]
    check("every issue has rule/severity/handle/layer/sample/suggest", not bad, bad[:1])
    check("every issue severity is blocker|major|minor",
          all(i["severity"] in ("blocker", "major", "minor") for i in report["issues"]))
    check("every issue has a non-empty sample",
          all(str(i["sample"]).strip() for i in report["issues"]))
    check("every issue has a non-empty suggest",
          all(str(i["suggest"]).strip() for i in report["issues"]))
    # duplicate text must be reported by duplicate_entity, not by text_overlap twice
    overlap_handles = [i["handle"] for i in report["issues"] if i["rule"] == "text_overlap"]
    check("text_overlap does not report the same handle twice",
          len(overlap_handles) == len(set(overlap_handles)), overlap_handles)
    # title exemption must not hide the mid-size outlier
    heights = [i for i in report["issues"] if i["rule"] == "text_height_outlier"]
    check("text_height_outlier reports the mid-size outlier too", len(heights) >= 2, heights)
    warnings = {w["code"] for w in report["summary"]["warnings"]}
    check("title exemption is disclosed in warnings", "TEXT_TITLE_EXEMPTED" in warnings, warnings)
    check("unknown linetype set is disclosed in warnings", "LINETYPE_SET_UNKNOWN" in warnings, warnings)


def test_clean_offline():
    report, _proc = audit(fixture("clean.info.json"), fixture("clean.read.json"),
                          extra=["--exit-code"], expect_exit=0)
    if not check("clean report generated", report is not None):
        return
    check("clean drawing has zero issues", report["summary"]["issues"] == 0,
          report["summary"]["byRule"])
    check("clean drawing passes the gate", report["gate"]["pass"] is True, report["gate"])
    check("clean drawing has no blockers/majors",
          report["summary"]["bySeverity"]["blocker"] == 0 and report["summary"]["bySeverity"]["major"] == 0,
          report["summary"]["bySeverity"])


def test_strict_offline():
    report, _proc = audit(fixture("strict.info.json"), fixture("strict.read.json"))
    if not check("strict report generated", report is not None):
        return
    counts = rule_counts(report)
    check("strict layer_naming >= 2 (space + case duplicate)", counts.get("layer_naming", 0) >= 2, counts)
    check("strict layer_unknown == 1", counts.get("layer_unknown", 0) == 1, counts)
    check("strict text_style_missing == 1", counts.get("text_style_missing", 0) == 1, counts)
    # default allow_cjk=True: the Chinese layer name alone must NOT be a naming issue
    naming = [i for i in report["issues"] if i["rule"] == "layer_naming"]
    check("default config does not flag the Chinese layer name",
          all("水系" not in (i["layer"] or "") for i in naming), [i["layer"] for i in naming])

    cfg = os.path.join(TMP, "strict-cjk.json")
    with open(cfg, "w", encoding="utf-8") as fh:
        json.dump({"layer_naming": {"allow_cjk": False}}, fh)
    report2, _ = audit(fixture("strict.info.json"), fixture("strict.read.json"), extra=["--config", cfg])
    counts2 = rule_counts(report2)
    check("strict config allow_cjk=false flags the Chinese layer name",
          counts2.get("layer_naming", 0) >= 3, counts2)
    cjk = [i for i in report2["issues"] if i["rule"] == "layer_naming" and "水系" in (i["layer"] or "")]
    check("cjk naming issue mentions the layer", len(cjk) == 1, cjk)


def test_severity_override():
    cfg = os.path.join(TMP, "sev.json")
    with open(cfg, "w", encoding="utf-8") as fh:
        json.dump({"severity": {"duplicate_entity": "blocker"}}, fh)
    report, _ = audit(fixture("problem-rich.info.json"), fixture("problem-rich.read.json"),
                      extra=["--config", cfg, "--exit-code"], expect_exit=3)
    sev = report["summary"]["bySeverity"]
    check("severity override turns duplicates into blockers", sev["blocker"] >= 3, sev)


def test_rules_filter():
    report, _ = audit(fixture("problem-rich.info.json"), fixture("problem-rich.read.json"),
                      extra=["--rules", "duplicate_entity,zero_length_line"])
    counts = rule_counts(report)
    check("--rules only runs the selected rules",
          set(counts) <= {"duplicate_entity", "zero_length_line"}, counts)
    check("--rules still finds its hits",
          counts.get("duplicate_entity", 0) >= 3 and counts.get("zero_length_line", 0) >= 1, counts)
    proc = run(["--read", fixture("problem-rich.read.json"), "--rules", "nope"], expect_exit=2)
    check("unknown rule id is rejected", proc.returncode == 2 and "unknown rule" in proc.stderr)


def test_issue_cap():
    cfg = os.path.join(TMP, "cap.json")
    with open(cfg, "w", encoding="utf-8") as fh:
        json.dump({"max_issues_per_rule": 1}, fh)
    report, _ = audit(fixture("problem-rich.info.json"), fixture("problem-rich.read.json"),
                      extra=["--config", cfg])
    by_rule = report["summary"]["byRule"]
    dup = by_rule.get("duplicate_entity", {})
    check("cap limits listed issues", dup.get("reported", 99) <= 1, dup)
    check("cap keeps the true total", dup.get("count", 0) >= 3, dup)
    check("cap records the suppressed amount", dup.get("suppressed", 0) >= 2, dup)


def test_no_info_mode():
    report, _proc = audit(None, fixture("problem-rich.read.json"))
    if not check("read-only report generated", report is not None):
        return
    skipped = skipped_rules(report)
    for rule in ("units_missing", "layer_empty", "layer_naming", "linetype_scale"):
        check("without info, %s is skipped as no_input" % rule,
              skipped.get(rule) == "no_input", skipped.get(rule))
    # no info means no EXTMIN/EXTMAX: outside_extents falls back to the entity-distribution
    # approximation instead of skipping (and says so)
    warnings = {w["code"] for w in report["summary"]["warnings"]}
    check("without info, outside_extents uses the disclosed approximation",
          "EXTENTS_STALE" in warnings and "outside_extents" not in skipped, warnings)
    entity_rules = {"zero_length_line", "duplicate_entity", "text_empty"}
    check("entity rules still run without info",
          entity_rules.issubset(set(rule_counts(report))), rule_counts(report))
    check("without info there is no blocker", report["gate"]["pass"] is True, report["gate"])


def test_bad_input():
    proc = run(["--read", os.path.join(TMP, "does-not-exist.json")], expect_exit=2)
    check("missing input file exits 2", proc.returncode == 2)
    broken = os.path.join(TMP, "broken.json")
    with open(broken, "w", encoding="utf-8") as fh:
        fh.write("{not json")
    proc = run(["--read", broken], expect_exit=2)
    check("invalid JSON exits 2", proc.returncode == 2)
    failed = os.path.join(TMP, "failed.json")
    with open(failed, "w", encoding="utf-8") as fh:
        json.dump({"ok": False, "error": {"code": "NO_INSTANCE", "message": "no CAD"}}, fh)
    proc = run(["--read", failed], expect_exit=2)
    check("ok:false zwc payload is rejected", "NO_INSTANCE" in proc.stderr or proc.returncode == 2)


def test_markdown_report():
    md = os.path.join(TMP, "report.md")
    js = os.path.join(TMP, "report.json")
    run(["--info", fixture("problem-rich.info.json"), "--read", fixture("problem-rich.read.json"),
         "--md", md, "--json", js], expect_exit=0)
    check("markdown report written", os.path.exists(md))
    if os.path.exists(md):
        with open(md, encoding="utf-8") as fh:
            text = fh.read()
        check("markdown has the title", "# 图纸体检报告" in text)
        check("markdown lists sections", "## 一、结论" in text and "## 三、问题清单" in text
              and "## 四、未执行的检查" in text)
        check("markdown separates the severity groups",
              "### 阻断（blocker" in text and "### 重要（major" in text)
        check("markdown report is substantial", len(text) > 2000, len(text))
    print("    (report sample: %s)" % md)


def test_real_snapshots():
    # name, max issues, max blockers, needs info?
    cases = [
        ("real-map", 5, 0, True),          # real 624-entity water-system map (snapshot not shipped -> SKIP)
        ("clean", 0, 0, True),            # real clean section-style drawing read back from CAD
        ("section-zwcad16", 3, 0, False),  # real 87-entity section drawing (read.json only)
    ]
    for name, max_issues, max_blockers, needs_info in cases:
        info_path = real(name + ".info.json")
        read_path = real(name + ".read.json")
        if not os.path.exists(read_path) or (needs_info and not os.path.exists(info_path)):
            skip("real snapshot " + name, "tests/audit/real/" + name + ".*.json not present")
            continue
        report, _proc = audit(info_path if os.path.exists(info_path) else None, read_path)
        if not check("real %s report generated" % name, report is not None):
            continue
        total = report["summary"]["issues"]
        blockers = report["summary"]["bySeverity"]["blocker"]
        check("real %s stays under the false-positive budget (<= %d issues)" % (name, max_issues),
              total <= max_issues, "got %d: %s" % (total, rule_counts(report)))
        check("real %s has no blockers (<= %d)" % (name, max_blockers), blockers <= max_blockers, blockers)


def main():
    if "--regen" in sys.argv:
        gen = os.path.join(HERE, "make_fixtures.py")
        subprocess.run([sys.executable, gen], check=True)
        clean = os.path.join(HERE, "make_clean_dxf.py")
        if os.path.exists(clean):
            subprocess.run([sys.executable, clean], check=False)
    test_rule_catalog()
    test_problem_rich_offline()
    test_clean_offline()
    test_strict_offline()
    test_severity_override()
    test_rules_filter()
    test_issue_cap()
    test_no_info_mode()
    test_bad_input()
    test_markdown_report()
    test_real_snapshots()

    print("")
    print("=" * 60)
    print("PASS %d / FAIL %d / SKIP %d" % (PASS, FAIL, SKIP))
    if FAILURES:
        print("failures:")
        for name, detail in FAILURES:
            print("  - " + name + ("  :: " + str(detail) if detail else ""))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
