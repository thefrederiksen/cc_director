---
name: bug-fixer
description: Fix GitHub issues by analyzing the issue, creating a fix plan, and implementing with user approval. Use when user provides an issue number and asks to fix it, or mentions "fix bug", "bug #", or "issue #".
---

# Bug Fixer Skill

Fix GitHub issues with a structured workflow that requires user approval before implementation.

## CRITICAL: User Approval Required

**NEVER implement a fix without explicit user approval.**

The workflow is:
1. Analyze issue and codebase
2. Present fix plan to user
3. **WAIT FOR APPROVAL** - User must say "yes", "approved", "go ahead", etc.
4. Only then: implement, build, commit, push, update issue

## Workflow

### Step 1: Get Issue Details

Fetch the issue from GitHub:
```bash
gh issue view {ISSUE_NUMBER} --json title,body,labels,state
```

Extract:
- Title
- Description
- Labels
- Any attachments/screenshots mentioned

### Step 2: Analyze the Issue

Search the codebase to understand:
- What code is involved
- What the root cause is
- Whether it's fixable via code changes

Use Grep, Glob, and Read tools to find relevant code.

**IMPORTANT: Check if Issue is Already Fixed**

Before proposing a fix, check recent commits to see if this issue was already addressed:
```bash
git log --oneline -20
git log --all --grep="{keyword from issue}" --oneline
```

Look for:
- Commits mentioning the issue number
- Commits mentioning keywords from the issue description
- Recent changes to files that would be involved in the fix

**If issue appears ALREADY FIXED**, report to user:
```
## Issue #{id} - Appears Already Fixed

### ANALYSIS
I analyzed the issue and found evidence that this has already been addressed:

**Recent Relevant Commit(s):**
- `{commit_hash}` - {commit message}

**Evidence:**
- {What code I found that addresses the issue}
- {Why I believe this fixes the reported problem}

### RECOMMENDATION
This issue appears to be already fixed. I recommend:
1. Testing the current behavior to confirm the fix works
2. If confirmed fixed, close the issue
3. If still occurring, provide more details about reproduction steps

**Would you like me to:**
- [ ] Close this issue as already fixed
- [ ] Investigate further with specific reproduction steps
- [ ] Something else

Please advise how to proceed.
```

**If NOT code-fixable**, report to user:
```
Issue #{id} is not a code fix. It requires:
- [ ] Configuration change
- [ ] Documentation update
- [ ] Infrastructure change
- [ ] More information needed

Recommendation: {what should be done}
```

**If CANNOT understand or reproduce the issue**, ask for clarification:
```
## Issue #{id} - Needs Clarification

### WHAT I FOUND
{Description of what I searched for and found}

### WHAT I DON'T UNDERSTAND
{Specific questions about the issue}

### QUESTIONS FOR USER
1. {Question 1}
2. {Question 2}

Please provide more details so I can properly analyze this issue.
```

### Step 3: Present Fix Plan (APPROVAL CHECKPOINT)

Present a structured plan using this format:

```
## BUG FIX PLAN - Issue #{id}

### PROBLEM
{What the user is experiencing - clear description of the symptom}

### ROOT CAUSE
{Technical explanation of why this happens}
- File: `{path/to/file}`
- Line: {line_number}
- Issue: {what the code does wrong}

### THE FIX
What we're changing:
1. {Change 1}
2. {Change 2}

### WHY THIS FIXES IT
{Explanation of how changes address root cause}

### FILES TO MODIFY
| File | Change |
|------|--------|
| {file1} | {description} |
| {file2} | {description} |

### RISK ASSESSMENT
**Risk Level:** {Low/Medium/High}
**Potential Side Effects:** {description or "None"}

---

**Approve this fix?** Reply "yes" to proceed with implementation, commit, and issue update.
```

### Step 4: Wait for Approval

**DO NOT PROCEED** until user explicitly approves.

Valid approval responses:
- "yes"
- "approved"
- "go ahead"
- "do it"
- "proceed"
- "ok"

If user says "no" or asks for changes, revise the plan and present again.

### Step 5: Implement the Fix

After approval:

1. **Make code changes** using Edit tool
2. **MANDATORY: Full Solution Build**

   **CRITICAL: You MUST build the ENTIRE solution before committing. No exceptions.**

   ```bash
   dotnet build cc_director.sln
   ```

   - Use the full solution file `cc_director.sln` - NEVER build individual projects
   - Wait for the build to complete fully
   - Check output for ANY errors or warnings that indicate problems

3. **Verify Build Success**

   The build MUST show:
   - `Build succeeded.`
   - `0 Error(s)`

   **DO NOT PROCEED TO COMMIT** if:
   - There are any build errors
   - The build did not complete
   - You see "Build FAILED"

4. **If build fails:**
   - Analyze the error messages
   - Fix the issues in your code
   - Run the full build again: `dotnet build cc_director.sln`
   - Repeat until build succeeds with 0 errors

**Why Full Build is Required:**
- Individual project builds may miss dependency issues
- Only the full solution build catches cross-project problems
- This ensures the entire codebase compiles correctly with your changes

### Step 6: Commit and Push

**Commit format:**
```bash
git add {files}
git commit -m "$(cat <<'EOF'
Fix {short description} (Issue #{id})

{Detailed explanation - 1-3 sentences}

Fixes #{id}
EOF
)"
```

**Push:**
```bash
git push
```

If push fails due to remote changes:
```bash
git pull --rebase && git push
```

### Step 7: Update GitHub Issue

Add a comment to the issue with the fix details:

```bash
gh issue comment {ISSUE_NUMBER} --body "## Fix Implemented

**Commit:** {short_hash}
**Build:** SUCCESS (0 errors)

### Changes
- **{file1}:** {description}
- **{file2}:** {description}

### How to Test
1. {Step 1}
2. {Step 2}
3. {Step 3}

**Expected Result:** {What the tester should see after the fix}

---
Generated with Claude Code"
```

If the issue should be closed:
```bash
gh issue close {ISSUE_NUMBER}
```

### Step 8: Report Completion

Report to user:
```
## Issue #{id} Fixed

- Commit: {hash}
- Files changed: {count}
- Build: SUCCESS
- Issue: Updated with fix details

The fix is ready for testing.
```

## Example Session

**User:** Fix issue 42

**Agent:** Let me analyze issue #42...

[Fetches issue, analyzes code, presents plan]

```
## BUG FIX PLAN - Issue #42

### PROBLEM
Session list doesn't refresh when a session is closed.

### ROOT CAUSE
- File: `src/CcDirector.Wpf/MainWindow.xaml.cs`
- Line: 234
- Issue: Session removal not triggering PropertyChanged

### THE FIX
What we're changing:
1. Add call to OnPropertyChanged after removing session
2. Ensure UI dispatcher is used for collection modification

### WHY THIS FIXES IT
The ObservableCollection notification wasn't being raised properly.

### FILES TO MODIFY
| File | Change |
|------|--------|
| MainWindow.xaml.cs | Add property notification |

### RISK ASSESSMENT
**Risk Level:** Low
**Potential Side Effects:** None

---

**Approve this fix?** Reply "yes" to proceed.
```

**User:** yes

**Agent:** [Implements fix, builds, commits, pushes, updates issue]

Final report to user:
```
## Issue #42 Fixed

- Commit: abc1234
- Files changed: 1
- Build: SUCCESS
- Issue: Updated with fix details

The fix is ready for testing.
```

## Commands Reference

### Fetch Issue
```bash
gh issue view {ID} --json title,body,labels,state
```

### Add Comment
```bash
gh issue comment {ID} --body "{comment}"
```

### Close Issue
```bash
gh issue close {ID}
```

### Build Solution (MANDATORY before commit)
```bash
dotnet build cc_director.sln
```
**ALWAYS use the full solution file. NEVER build individual .csproj files.**

### Run Tests
```bash
dotnet test src/CcDirector.Core.Tests
```

### Git Workflow
```bash
git status
git diff --stat
git add {files}
git commit -m "{message}"
git push
```

## Important Notes

1. **FULL SOLUTION BUILD IS MANDATORY** - Always run `dotnet build cc_director.sln` before committing. Never build individual projects. Never skip this step. Never commit if build fails.
2. **One issue per commit** - Keep commits focused on single issue fix
3. **Reference issue in commit** - Include "Fixes #{id}" for auto-close
4. **Update GitHub** - Always update issue state and add comment after fix
5. **Report clearly** - User should know exactly what was changed

## When NOT to Use This Skill

- Issue requires manual testing only (no code change)
- Issue is a feature request (use different workflow)
- Issue requires external dependencies or configuration changes

## Special Cases This Skill Handles

- **Already Fixed Issues**: Checks git history and reports if issue appears resolved
- **Unclear Issues**: Asks clarifying questions instead of guessing
- **Non-Code Fixes**: Reports what type of fix is needed (config, docs, etc.)

---

**Skill Version:** 1.0
**Last Updated:** 2026-02-14
**Adapted from:** mindzieWeb bug-fixer skill (changed from Azure DevOps to GitHub)
