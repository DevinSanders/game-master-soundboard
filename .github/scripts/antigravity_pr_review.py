#!/usr/bin/env python3
"""
Antigravity Pull Request Reviewer & Auto-Merge Engine
Automated code review, architectural guideline enforcement, and dependency auto-merging.
"""

import os
import sys
import json
import subprocess
import urllib.request
import urllib.error
from typing import Dict, Any, List, Optional, Tuple


def run_command(cmd: List[str], check: bool = True) -> Tuple[int, str, str]:
    """Execute a subprocess command safely and return (exit_code, stdout, stderr)."""
    proc = subprocess.run(
        cmd,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8"
    )
    if check and proc.returncode != 0:
        print(f"[ERROR] Command failed ({proc.returncode}): {' '.join(cmd)}\n{proc.stderr}", file=sys.stderr)
    return proc.returncode, proc.stdout, proc.stderr


def get_pr_details(pr_number: int) -> Optional[Dict[str, Any]]:
    """Retrieve PR metadata using GitHub CLI."""
    fields = "number,title,body,author,labels,isDraft,state,headRefName,baseRefName,mergeable,headRefOid"
    code, stdout, stderr = run_command(["gh", "pr", "view", str(pr_number), "--json", fields], check=False)
    if code != 0 or not stdout.strip():
        print(f"[WARN] Failed to fetch PR #{pr_number} metadata: {stderr.strip()}", file=sys.stderr)
        return None
    try:
        return json.loads(stdout)
    except Exception as e:
        print(f"[ERROR] Error parsing PR metadata JSON: {e}", file=sys.stderr)
        return None


def get_pr_diff(pr_number: int, max_chars: int = 120_000) -> str:
    """Fetch PR diff via GitHub CLI, truncated cleanly if too large."""
    code, stdout, stderr = run_command(["gh", "pr", "diff", str(pr_number)], check=False)
    if code != 0:
        print(f"[WARN] Failed to get diff for PR #{pr_number}: {stderr.strip()}", file=sys.stderr)
        return ""
    diff = stdout.strip()
    if len(diff) > max_chars:
        diff = diff[:max_chars] + f"\n\n... [Diff truncated by Antigravity: {len(stdout) - max_chars} characters omitted]"
    return diff


def get_pr_checks(pr_number: int) -> Dict[str, Any]:
    """Retrieve CI status check results for a PR."""
    code, stdout, stderr = run_command(["gh", "pr", "checks", str(pr_number), "--json", "name,state,bucket"], check=False)
    if code != 0 or not stdout.strip():
        return {"checks": [], "all_passed": False, "any_failed": False, "is_pending": True}
    try:
        checks = json.loads(stdout)
        if not checks:
            return {"checks": [], "all_passed": False, "any_failed": False, "is_pending": True}

        all_passed = all(c.get("bucket") == "pass" or c.get("state") == "SUCCESS" for c in checks)
        any_failed = any(c.get("bucket") == "fail" or c.get("state") == "FAILURE" for c in checks)
        is_pending = any(c.get("bucket") == "pending" or c.get("state") in ("PENDING", "QUEUED", "IN_PROGRESS") for c in checks)
        return {
            "checks": checks,
            "all_passed": all_passed,
            "any_failed": any_failed,
            "is_pending": is_pending,
        }
    except Exception as e:
        print(f"[ERROR] Error parsing checks JSON: {e}", file=sys.stderr)
        return {"checks": [], "all_passed": False, "any_failed": False, "is_pending": True}


def get_existing_bot_reviews(pr_number: int) -> List[Dict[str, Any]]:
    """Fetch list of reviews on the PR from actions / bots."""
    code, stdout, stderr = run_command(["gh", "pr", "view", str(pr_number), "--json", "reviews"], check=False)
    if code != 0 or not stdout.strip():
        return []
    try:
        data = json.loads(stdout)
        reviews = data.get("reviews", [])
        bot_reviews = []
        for r in reviews:
            author = r.get("author", {})
            login = author.get("login", "") if isinstance(author, dict) else str(author)
            if "github-actions" in login or "antigravity" in login.lower() or "bot" in login.lower():
                bot_reviews.append(r)
        return bot_reviews
    except Exception:
        return []


def read_repository_guidelines() -> str:
    """Read local codebase rules and architecture guidelines."""
    guideline_files = [
        ".github/copilot-instructions.md",
        "CONTRIBUTING.md",
        "CLAUDE.md",
        ".github/dependabot.yml"
    ]
    sections = []
    for path in guideline_files:
        if os.path.exists(path):
            try:
                with open(path, "r", encoding="utf-8") as f:
                    sections.append(f"### File: {path}\n{f.read()[:6000]}")
            except Exception as e:
                print(f"[INFO] Could not read {path}: {e}")
    return "\n\n".join(sections)


def is_dependency_pr(pr_details: Dict[str, Any]) -> bool:
    """Determine if PR is from Dependabot or is a dependency update."""
    author_info = pr_details.get("author", {})
    author_login = author_info.get("login", "") if isinstance(author_info, dict) else str(author_info)
    is_bot = author_info.get("is_bot", False) if isinstance(author_info, dict) else False

    if "dependabot" in author_login.lower() or (is_bot and "dependabot" in author_login.lower()):
        return True

    labels = [l.get("name", "").lower() for l in pr_details.get("labels", []) if isinstance(l, dict)]
    if any(l in ("dependencies", "dependabot", "deps", "nuget") for l in labels):
        return True

    head_ref = pr_details.get("headRefName", "").lower()
    if head_ref.startswith("dependabot/"):
        return True

    return False


def call_gemini_api(prompt: str, api_key: str, model: str = "gemini-2.5-flash") -> Optional[Dict[str, Any]]:
    """Call Google Gemini API using Python standard library urllib."""
    url = f"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={api_key}"
    headers = {"Content-Type": "application/json"}
    payload = {
        "contents": [
            {
                "parts": [
                    {"text": prompt}
                ]
            }
        ],
        "generationConfig": {
            "temperature": 0.2,
            "responseMimeType": "application/json"
        }
    }

    req = urllib.request.Request(url, data=json.dumps(payload).encode("utf-8"), headers=headers, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=90) as resp:
            data = json.loads(resp.read().decode("utf-8"))
            candidates = data.get("candidates", [])
            if not candidates:
                return None
            parts = candidates[0].get("content", {}).get("parts", [])
            if not parts:
                return None
            text = parts[0].get("text", "")
            return json.loads(text)
    except urllib.error.HTTPError as e:
        err_msg = e.read().decode("utf-8", errors="ignore")
        print(f"[ERROR] Gemini API HTTP Error {e.code}: {err_msg}", file=sys.stderr)
        return None
    except Exception as e:
        print(f"[ERROR] Gemini API invocation failed: {e}", file=sys.stderr)
        return None


def build_review_prompt(pr_details: Dict[str, Any], diff: str, guidelines: str) -> str:
    return f"""You are Antigravity, an expert software architect and automated code reviewer reviewing a pull request for this .NET repository.

Repository guidelines and architectural constraints:
{guidelines}

Pull Request Details:
- Number: #{pr_details.get('number')}
- Title: {pr_details.get('title')}
- Author: {pr_details.get('author', {}).get('login')}
- Target Branch: {pr_details.get('baseRefName')}
- Source Branch: {pr_details.get('headRefName')}
- Description:
{pr_details.get('body', '')[:2000]}

Git Diff:
```diff
{diff}
```

Instructions:
1. Thoroughly inspect the diff against repository conventions:
   - Layered architecture: Desktop -> UI -> Core -> PluginApi (no Avalonia in Core; reflection for Windows NAudio in Core).
   - Audio pipeline safety (WaveBuffer aliasing rules; float conversions).
   - SQLite migrations (append-only in SchemaMigrations.cs).
   - MVVM patterns (CommunityToolkit.Mvvm, ObservableProperty, compiled bindings).
   - For dependency/NuGet upgrades: check for breaking changes, transitive package downgrades (NU1605), or licensing issues (e.g. FluentAssertions 8+ license change).
2. Choose one Verdict:
   - "APPROVE": Changes are safe, adhere to conventions, and no breaking bugs/violations were found.
   - "COMMENT": Minor suggestions, advisory notes, or informative feedback; non-blocking.
   - "REQUEST_CHANGES": Identified breaking changes, architectural boundary violations, licensing pitfalls, or bugs.
3. If this is a Dependabot/dependency PR, evaluate whether the bump is safe to auto-merge. If it introduces breaking changes or requires manual code adaptation, explain why.

Return ONLY a valid JSON object matching this schema:
{{
  "verdict": "APPROVE" | "COMMENT" | "REQUEST_CHANGES",
  "summary": "Concise 1-3 sentence summary of the PR and impact.",
  "confidence_score": 95,
  "assessment": {{
    "architecture": "Assessment of architectural layer compliance",
    "correctness": "Assessment of runtime correctness, null safety, bugs",
    "dependencies": "Assessment of package compatibility, breaking changes, licenses"
  }},
  "findings": [
    {{
      "severity": "info" | "warning" | "error",
      "message": "Specific finding explanation",
      "suggestion": "Actionable recommendation or fix"
    }}
  ],
  "auto_merge_recommendation": {{
    "eligible": true,
    "reason": "Clear explanation of why it is or is not safe for auto-merge"
  }}
}}
"""


def format_review_markdown(review: Dict[str, Any], pr_details: Dict[str, Any]) -> str:
    verdict = review.get("verdict", "COMMENT")
    verdict_badge = {
        "APPROVE": "✅ **APPROVE**",
        "COMMENT": "💬 **COMMENT**",
        "REQUEST_CHANGES": "❌ **REQUEST CHANGES**"
    }.get(verdict, verdict)

    lines = [
        "## 🤖 Antigravity Automated PR Review",
        "",
        f"**Verdict**: {verdict_badge}  ",
        f"**Confidence**: {review.get('confidence_score', 90)}%",
        "",
        f"### 📋 Summary\n{review.get('summary', 'No summary provided.')}",
        "",
        "### 🏗️ Architectural & Quality Assessment",
    ]

    assess = review.get("assessment", {})
    if assess.get("architecture"):
        lines.append(f"- **Architecture**: {assess['architecture']}")
    if assess.get("correctness"):
        lines.append(f"- **Correctness**: {assess['correctness']}")
    if assess.get("dependencies"):
        lines.append(f"- **Dependencies & Licensing**: {assess['dependencies']}")

    findings = review.get("findings", [])
    if findings:
        lines.extend(["", "### 🔍 Key Findings & Suggestions"])
        for f in findings:
            sev_icon = {"error": "🚫", "warning": "⚠️", "info": "ℹ️"}.get(f.get("severity"), "•")
            lines.append(f"- {sev_icon} **{f.get('severity', 'Note').title()}**: {f.get('message')}")
            if f.get("suggestion"):
                lines.append(f"  *Suggestion*: {f.get('suggestion')}")

    auto_merge = review.get("auto_merge_recommendation", {})
    lines.extend([
        "",
        "### ⚡ Auto-Merge Status",
        f"- **Eligible for Auto-Merge**: {'Yes' if auto_merge.get('eligible') else 'No'}",
        f"- **Reason**: {auto_merge.get('reason', 'N/A')}",
        "",
        "---",
        "*Automated by [Google Antigravity](https://antigravity.google) PR Review & Merge Gate*"
    ])

    return "\n".join(lines)


def submit_review_to_github(pr_number: int, review: Dict[str, Any], pr_details: Dict[str, Any]) -> bool:
    """Submit the review to GitHub using gh CLI."""
    body = format_review_markdown(review, pr_details)
    verdict = review.get("verdict", "COMMENT")

    review_flag = "--comment"
    if verdict == "APPROVE":
        review_flag = "--approve"
    elif verdict == "REQUEST_CHANGES":
        review_flag = "--request-changes"

    cmd = ["gh", "pr", "review", str(pr_number), review_flag, "--body", body]
    code, stdout, stderr = run_command(cmd, check=False)
    if code == 0:
        print(f"[SUCCESS] Submitted review ({verdict}) for PR #{pr_number}")
        return True
    else:
        # Fallback to comment if approve/request-changes is restricted
        print(f"[WARN] Failed to submit review as {review_flag} ({stderr.strip()}). Falling back to comment...", file=sys.stderr)
        fallback_cmd = ["gh", "pr", "review", str(pr_number), "--comment", "--body", body]
        fb_code, _, fb_err = run_command(fallback_cmd, check=False)
        return fb_code == 0


def evaluate_and_automerge(pr_number: int, pr_details: Dict[str, Any], review: Dict[str, Any]) -> None:
    """Evaluate and handle auto-merging according to policy."""
    is_dep = is_dependency_pr(pr_details)
    verdict = review.get("verdict")
    auto_eligible = review.get("auto_merge_recommendation", {}).get("eligible", False)

    print(f"\n[INFO] Evaluating Auto-Merge Policy for PR #{pr_number}:")
    print(f"       - Is Dependency Update: {is_dep}")
    print(f"       - Review Verdict: {verdict}")
    print(f"       - Review Auto-Merge Eligible: {auto_eligible}")

    if not is_dep:
        print(f"[POLICY] PR #{pr_number} is not a dependency update. Non-dependency PRs require manual maintainer approval.")
        return

    if verdict != "APPROVE" or not auto_eligible:
        print(f"[POLICY] PR #{pr_number} did not receive approval for auto-merge. Manual review required.")
        return

    checks_info = get_pr_checks(pr_number)
    print(f"[INFO] CI Checks Status: all_passed={checks_info['all_passed']}, any_failed={checks_info['any_failed']}, is_pending={checks_info['is_pending']}")

    if checks_info["any_failed"]:
        print(f"[BLOCKED] PR #{pr_number} has failing CI checks. Auto-merge aborted.")
        return

    if checks_info["all_passed"] and len(checks_info["checks"]) > 0:
        print(f"[INFO] All CI checks passed! Merging PR #{pr_number} via squash merge...")
        merge_cmd = [
            "gh", "pr", "merge", str(pr_number),
            "--squash",
            "--delete-branch",
            "--subject", f"Merge PR #{pr_number}: {pr_details.get('title', '')}"
        ]
        code, out, err = run_command(merge_cmd, check=False)
        if code == 0:
            print(f"[SUCCESS] 🚀 PR #{pr_number} successfully auto-merged!")
        else:
            print(f"[ERROR] Could not complete merge: {err.strip() or out.strip()}", file=sys.stderr)
    else:
        # Checks are still pending/in-progress; enable GitHub native auto-merge
        print(f"[INFO] CI checks are still pending. Scheduling native GitHub auto-merge on PR #{pr_number}...")
        auto_cmd = ["gh", "pr", "merge", str(pr_number), "--auto", "--squash", "--delete-branch"]
        code, out, err = run_command(auto_cmd, check=False)
        if code == 0:
            print(f"[SUCCESS] Native auto-merge scheduled for PR #{pr_number}.")
        else:
            print(f"[INFO] gh pr merge --auto result: {err.strip() or out.strip()}")


def handle_workflow_run_event(event_data: Dict[str, Any]) -> None:
    """Handle workflow_run completion event (e.g. Build matrix finished)."""
    workflow_run = event_data.get("workflow_run", {})
    conclusion = workflow_run.get("conclusion")
    prs = workflow_run.get("pull_requests", [])

    print(f"[INFO] Workflow Run '{workflow_run.get('name')}' completed with conclusion: {conclusion}")
    if conclusion != "success":
        print(f"[INFO] Workflow did not succeed ({conclusion}). Skipping merge checks.")
        return

    if not prs:
        print("[INFO] No pull requests associated with this workflow run.")
        return

    for pr_summary in prs:
        pr_number = pr_summary.get("number")
        if not pr_number:
            continue
        print(f"\n[INFO] Checking PR #{pr_number} after successful Build workflow run...")
        pr_details = get_pr_details(pr_number)
        if not pr_details:
            continue

        if not is_dependency_pr(pr_details):
            print(f"[POLICY] PR #{pr_number} is not a dependency update. Skipping auto-merge.")
            continue

        bot_reviews = get_existing_bot_reviews(pr_number)
        approved_by_bot = any(r.get("state") == "APPROVED" for r in bot_reviews)
        print(f"[INFO] PR #{pr_number} approved by Antigravity bot: {approved_by_bot}")

        if approved_by_bot:
            checks_info = get_pr_checks(pr_number)
            if checks_info["all_passed"] and not checks_info["any_failed"]:
                print(f"[INFO] All conditions met! Auto-merging PR #{pr_number}...")
                merge_cmd = [
                    "gh", "pr", "merge", str(pr_number),
                    "--squash",
                    "--delete-branch",
                    "--subject", f"Merge PR #{pr_number}: {pr_details.get('title', '')}"
                ]
                code, out, err = run_command(merge_cmd, check=False)
                if code == 0:
                    print(f"[SUCCESS] 🚀 Auto-merged PR #{pr_number} following successful build!")
                else:
                    print(f"[ERROR] Merge command returned: {err.strip() or out.strip()}", file=sys.stderr)


def main():
    print("=======================================================")
    print("      Antigravity PR Reviewer & Auto-Merge Engine      ")
    print("=======================================================")

    event_name = os.environ.get("GITHUB_EVENT_NAME", "")
    event_path = os.environ.get("GITHUB_EVENT_PATH", "")

    # 1. Handle workflow_run event if triggered after Build completion
    if event_name == "workflow_run" and event_path and os.path.exists(event_path):
        with open(event_path, "r", encoding="utf-8") as f:
            event_data = json.load(f)
        handle_workflow_run_event(event_data)
        return

    # 2. Handle PR review trigger
    pr_number_str = os.environ.get("PR_NUMBER")
    if not pr_number_str and event_path and os.path.exists(event_path):
        with open(event_path, "r", encoding="utf-8") as f:
            event_data = json.load(f)
            pr_data = event_data.get("pull_request")
            if pr_data:
                pr_number_str = str(pr_data.get("number"))

    if not pr_number_str:
        print("[ERROR] PR_NUMBER environment variable or event data not found.", file=sys.stderr)
        sys.exit(1)

    pr_number = int(pr_number_str)
    print(f"[INFO] Processing PR #{pr_number}...")

    pr_details = get_pr_details(pr_number)
    if not pr_details:
        print(f"[ERROR] Could not fetch PR details for #{pr_number}.", file=sys.stderr)
        sys.exit(1)

    if pr_details.get("isDraft"):
        print(f"[INFO] PR #{pr_number} is currently a draft. Skipping review until ready for review.")
        return

    api_key = os.environ.get("GEMINI_API_KEY")
    if not api_key:
        print("\n[NOTICE] GEMINI_API_KEY is not configured in repository secrets.")
        print("To enable Antigravity AI reviews and auto-merge:")
        print("  1. Obtain a Gemini API key at https://aistudio.google.com/app/api-keys")
        print("  2. In your repository settings, add secret GEMINI_API_KEY under Actions Secrets:")
        print("     gh secret set GEMINI_API_KEY")
        print("Skipping AI review step until GEMINI_API_KEY is configured.\n")
        return

    diff = get_pr_diff(pr_number)
    if not diff:
        print(f"[WARN] No diff found for PR #{pr_number}.")
        return

    guidelines = read_repository_guidelines()
    model = os.environ.get("GEMINI_MODEL", "gemini-2.5-flash")

    print(f"[INFO] Querying Antigravity ({model}) for PR review...")
    prompt = build_review_prompt(pr_details, diff, guidelines)
    review = call_gemini_api(prompt, api_key, model=model)

    if not review:
        fallback_model = "gemini-2.5-flash" if model != "gemini-2.5-flash" else "gemini-1.5-flash"
        print(f"[WARN] {model} query failed. Attempting fallback to {fallback_model}...")
        review = call_gemini_api(prompt, api_key, model=fallback_model)

    if not review:
        print("[ERROR] Failed to obtain AI review response from Gemini API.", file=sys.stderr)
        sys.exit(1)

    print(f"[INFO] Review generated successfully. Verdict: {review.get('verdict')}")
    submit_review_to_github(pr_number, review, pr_details)

    # Evaluate auto-merge
    evaluate_and_automerge(pr_number, pr_details, review)


if __name__ == "__main__":
    main()
