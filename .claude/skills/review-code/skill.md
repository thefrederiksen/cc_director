---
name: review-code
description: Review recent code changes for security issues, PII leaks, and bugs against CodingStyle.md. Triggers on "/review-code" or when called by commit skill.
---

# Code Review Skill

Review changed files against docs/CodingStyle.md and check for PII/personal information leaks.

This is a **public repository**. Every commit is visible to the world. The PII check is mandatory.

## Triggers

Invoke with /review-code or when called by commit skill.

## Workflow

STEP 1: Get files to review

Use Bash tool to run: git diff --cached --name-only
Then run: git diff --name-only
Also run: git status to find untracked files.

Collect ALL files from the output, not just .cs files. PII can appear in ANY file type:
.cs, .xaml, .xaml.cs, .json, .md, .txt, .ps1, .bat, .sh, .config, .yaml, .yml, .xml, .csproj, etc.

STEP 2: Read the standards (MANDATORY)

Use the Read tool to read: docs/CodingStyle.md

Do NOT skip this. Do NOT rely on memory. Actually READ this file.

STEP 3: PII and Personal Information Scan (MANDATORY)

This is a PUBLIC REPOSITORY. Any personal information committed here is exposed to the entire internet.

For EVERY changed or untracked file, scan for ALL of the following:

BLOCKING PII (must NEVER be committed):
- Real names of people (developers, employees, users, customers)
- Email addresses (personal or corporate)
- Usernames, account names, Windows user profile names (e.g., "jsmith", "jdoe")
- File paths containing usernames (e.g., C:\Users\jsmith\..., /home/jdoe/...)
- Company names, organization names, internal project codenames
- IP addresses (internal or external, not localhost/127.0.0.1/::1)
- Phone numbers
- Physical addresses, office locations
- API keys, tokens, secrets, passwords (even if expired or fake-looking)
- Internal URLs (intranet, VPN, internal tools, Jira, Confluence)
- Employee IDs, badge numbers
- Machine names, server hostnames (not generic like "localhost")
- Database connection strings with server names
- Git remote URLs containing usernames or internal domains
- Screenshots or images that may contain PII (flag for manual review)
- Session IDs, auth tokens, or cookies from real sessions
- License keys or registration codes

IMPORTANT PATH PATTERNS to flag:
- C:\Users\[anyname]\... -> BLOCKING (exposes Windows username)
- /home/[anyname]/... -> BLOCKING (exposes Linux username)
- /Users/[anyname]/... -> BLOCKING (exposes macOS username)
- \\[servername]\... -> BLOCKING (exposes internal server name)

EXCEPTIONS (NOT PII - do not flag these):
- Generic placeholder paths in code comments explaining patterns (e.g., "E.g., C:\Users\username\...")
- Paths using environment variables (e.g., %USERPROFILE%, $HOME, ~/)
- Paths using AppContext.BaseDirectory, Path.GetTempPath(), Environment.GetFolderPath()
- "localhost", "127.0.0.1", "::1", "0.0.0.0"
- Example/placeholder values clearly marked as such (e.g., "user@example.com")
- Open-source project names and maintainer names from public packages
- Anthropic/Claude branding (this is a Claude Code tool - that's expected)

STEP 3.5: CenCon Documentation Check

Check if docs/cencon/architecture_manifest.yaml exists using the Glob tool.

If docs/cencon/architecture_manifest.yaml does NOT exist:
- Set CENCON_STATUS = SKIPPED
- Add SUGGESTION: "Consider adding CenCon documentation (docs/cencon/)"
- Continue to STEP 3.6

If docs/cencon/architecture_manifest.yaml EXISTS:
1. Read the manifest using the Read tool
2. Extract the last_updated field (format: YYYY-MM-DD)
3. Use Bash to find the most recently modified .cs file in src/:
   git log -1 --format="%ai" -- "src/**/*.cs"
4. Documentation is STALE if:
   - Any .cs file was modified MORE RECENTLY than the manifest's last_updated date
   - OR manifest last_updated is more than 30 days old
5. If STALE:
   - Add BLOCKING issue: "CenCon architecture_manifest.yaml is outdated"
   - Include: "Last manifest update: [date], Most recent code change: [date]"
   - Set CENCON_STATUS = FAIL
6. If CURRENT:
   - Set CENCON_STATUS = PASS

STEP 3.6: Security Profile Drift Check

Check if docs/cencon/security_profile.yaml exists using the Glob tool.

If docs/cencon/security_profile.yaml does NOT exist:
- Set SECURITY_DRIFT = SKIPPED
- Continue to STEP 4

If docs/cencon/security_profile.yaml EXISTS:
1. Read the profile using the Read tool
2. Extract the last_verified field (format: YYYY-MM-DD)
3. Calculate days since verification (use today's date)
4. Extract drift.threshold_days (default: 30)
5. If days since verification > threshold_days:
   - Add BLOCKING issue: "Security profile drift detected"
   - Include: "Last verified: [date], Days since: [N], Threshold: [threshold_days]"
   - Set SECURITY_DRIFT = FAIL
6. If within threshold:
   - Set SECURITY_DRIFT = PASS

STEP 4: Code style review

For each .cs, .xaml, and .xaml.cs file from Step 1:
- Use the Read tool to read the full file
- Compare against the rules from docs/CodingStyle.md
- Record issues with FULL PATH and line number

Issue severities:
- BLOCKING: Must fix. Causes review to FAIL.
- WARNING: Should fix. Review still PASSES.
- SUGGESTION: Nice to have. Review still PASSES.

STEP 5: Present findings

Use this exact format (plain text, no markdown tables):

Code Review Report

Files Reviewed: [count]
Standards Applied: CodingStyle.md, PII Scan
Result: PASS or FAIL

PII/PERSONAL INFORMATION Issues (BLOCKING - must fix before commit):

[full path]:[line]
PII Type: [what kind of PII was found]
Content: [the offending text, redacted if necessary]
Fix: [how to fix it - use environment variables, generics, or remove]

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

CRITICAL: Use FULL file paths like D:\Repos\cc_director\src\CcDirector.Core\Session.cs:45
Never use just the filename.

If NO PII is found, include this line in the report:
PII Scan: CLEAN - No personal information detected in changed files.

STEP 6: Return structured status

At the very end, include these lines exactly:

REVIEW_STATUS: PASS or FAIL
BLOCKING_COUNT: [number]
WARNING_COUNT: [number]
SUGGESTION_COUNT: [number]
PII_COUNT: [number]
CENCON_STATUS: PASS, FAIL, or SKIPPED
SECURITY_DRIFT: PASS, FAIL, or SKIPPED

FAIL if any of:
- BLOCKING issues exist
- PII issues exist
- CENCON_STATUS is FAIL
- SECURITY_DRIFT is FAIL

PASS if none of the above conditions are true. SKIPPED status does NOT cause FAIL.

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
PII scan applies to ALL file types, not just code files.

---

**Skill Version:** 3.0
**Last Updated:** 2026-02-21
**Adapted from:** mindzieWeb review-code skill
**CenCon Integration:** Added STEP 3.5 (Documentation Check) and STEP 3.6 (Security Drift Check)
