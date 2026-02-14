---
name: review-code
description: Review recent code changes for security issues and bugs against CodingStyle.md. Triggers on "/review-code" or when called by commit skill.
---

# Code Review Skill

Review changed files against docs/CodingStyle.md.

## Triggers

Invoke with /review-code or when called by commit skill.

## Workflow

STEP 1: Get files to review

Use Bash tool to run: git diff --cached --name-only
Then run: git diff --name-only

Collect all .cs, .xaml, and .xaml.cs files from the output.

STEP 2: Read the standards (MANDATORY)

Use the Read tool to read: docs/CodingStyle.md

Do NOT skip this. Do NOT rely on memory. Actually READ this file.

STEP 3: Review each changed file

For each file from Step 1:
- Use the Read tool to read the full file
- Compare against the rules from docs/CodingStyle.md
- Record issues with FULL PATH and line number

Issue severities:
- BLOCKING: Must fix. Causes review to FAIL.
- WARNING: Should fix. Review still PASSES.
- SUGGESTION: Nice to have. Review still PASSES.

STEP 4: Present findings

Use this exact format (plain text, no markdown tables):

Code Review Report

Files Reviewed: [count]
Standards Applied: CodingStyle.md
Result: PASS or FAIL

BLOCKING Issues (must fix before commit):

[full path]:[line]
Issue: [what is wrong]
Fix: [how to fix it]

WARNING Issues (should fix):

[full path]:[line]
Issue: [what is wrong]

SUGGESTIONS:

[full path]:[line]
Issue: [what could be improved]

CRITICAL: Use FULL file paths like D:\ReposFred\cc_director\src\CcDirector.Core\Session.cs:45
Never use just the filename.

STEP 5: Return structured status

At the very end, include these lines exactly:

REVIEW_STATUS: PASS or FAIL
BLOCKING_COUNT: [number]
WARNING_COUNT: [number]
SUGGESTION_COUNT: [number]

FAIL if any BLOCKING issues exist. PASS otherwise.

## Common Issues from CodingStyle.md

BLOCKING:
- Null-forgiving operator (!) to suppress warnings
- Using .Result or .Wait() on async
- Swallowing exceptions silently
- Fallback programming patterns
- Hard-coded credentials
- Missing FileLog.Write in public methods
- UI blocking operations (synchronous I/O on UI thread)
- Using git add . or git add -A

WARNING:
- Private fields without underscore prefix
- Methods over 50 lines
- async void methods (except event handlers)
- IDisposable without disposal
- Missing parameter validation at method start
- Dispatcher.Invoke instead of Dispatcher.BeginInvoke

## WPF-Specific Issues

BLOCKING:
- ObservableCollection modified from background thread without Dispatcher
- File I/O or network calls on UI thread without Task.Run

WARNING:
- Missing INotifyPropertyChanged on ViewModels
- UI elements without DataContext binding

## Notes

Focus on changed code, not legacy issues.
Be specific with line numbers.
The commit skill depends on the REVIEW_STATUS line.

---

**Skill Version:** 1.0
**Last Updated:** 2026-02-14
**Adapted from:** mindzieWeb review-code skill
