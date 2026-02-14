---
name: commit
description: Create a git commit following the project's commit standards. Triggers on "/commit" or when user asks to commit changes.
---

# Commit Skill

Commit workflow: review code, list files, get approval, commit, push.

## Triggers

Invoke with /commit or when user asks to commit changes.

## Scope

Default: Only files Claude modified this session.
With "all" argument: All uncommitted changes.
With specific files: Only those files.

## Workflow

STEP 1: Determine scope and gather changes

Run git status to see all changes.
Run git log --oneline -5 to see recent commit message style.

If user specified "all", include everything.
If user specified files, use those.
Otherwise, use only files modified during this Claude Code session.

STEP 2: Run code review

Use the Skill tool to invoke the review-code skill.

Wait for the review to complete.

Read the REVIEW_STATUS line from the output.

If REVIEW_STATUS is FAIL:
- Show the blocking issues found
- Tell the user they have three options:
  1. Reply "fix issues" for help resolving them
  2. Fix manually and run /commit again
  3. Reply "commit anyway" to bypass (not recommended)
- STOP and wait for user response

If REVIEW_STATUS is PASS:
- Note any warnings
- Continue to Step 3

STEP 3: Present commit plan

Show the user:
- List of files to commit (status and path)
- Code review result summary
- Proposed commit message

Commit message format:
  [type]: [Short description]

  [Optional body explaining why]

Type prefixes: feat, fix, refactor, docs, style, test, chore

Do NOT include "Co-Authored-By" lines in commit messages.

Ask: Approve this commit? Reply "yes" to commit and push.

STEP 4: Wait for approval

DO NOT PROCEED until user says yes, ok, approved, go ahead, or proceed.

If user says no or requests changes, update the plan and re-present.

STEP 5: Execute commit

Stage the specific files using git add with each filename.
Never use git add . or git add -A.

Commit using HEREDOC format:
git commit -m "$(cat <<'EOF'
[message here]
EOF
)"

Run git status to verify commit succeeded.

Push with git push.

If push fails due to remote changes, run git pull --rebase then git push.

STEP 6: Report completion

Tell the user:
- Commit hash
- Number of files changed
- Branch name
- Push status

## Handling bypass requests

If user says "commit anyway" with blocking issues:
- Warn about the risks (runtime errors, security, UI issues)
- Ask them to type "I understand the risks"
- If confirmed, add a note about bypassed review in the commit message
- Proceed with commit

## Git rules (from CLAUDE.md)

NEVER do these:
- Update git config
- Run push --force, reset --hard, checkout ., clean -f
- Skip hooks with --no-verify
- Force push to main/master
- Use git add . or git add -A
- Amend commits unless explicitly requested

ALWAYS do these:
- Use HEREDOC for commit messages
- Push after committing
- Create NEW commit after hook failures, never amend

---

**Skill Version:** 1.0
**Last Updated:** 2026-02-14
**Adapted from:** mindzieWeb commit skill
