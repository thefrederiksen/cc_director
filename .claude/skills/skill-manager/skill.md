---
name: skill-manager
description: Meta-skill for managing Claude Code skills. Create, review, edit, and list skills in a standardized way. Triggers on "/skill-manager", "create skill", "review skill", "list skills", "edit skill", "skill management".
---

# Skill Manager

A meta-skill for creating, reviewing, editing, and listing Claude Code skills.

## Quick Reference

| Command | Action |
|---------|--------|
| `/skill-manager create <name>` | Create a new skill with selected template |
| `/skill-manager review <name>` | Validate a skill for quality/completeness |
| `/skill-manager review all` | Review all skills in the project |
| `/skill-manager edit <name>` | Edit an existing skill |
| `/skill-manager list` | List all skills with descriptions |
| `/skill-manager list --categorize` | List skills grouped by category |

---

## MODE: CREATE

Create a new skill with proper structure and template.

### Workflow

1. **Parse skill name** from the command argument
2. **Check if skill exists** - if `.claude/skills/{name}/skill.md` exists, abort with error
3. **Present template options** - ask user to select a template type
4. **Create directory** - `.claude/skills/{name}/`
5. **Generate skeleton** - create `skill.md` using selected template
6. **Report success** - show path to new skill

### Template Selection

Present these options to the user:

| Template | Best For | Based On |
|----------|----------|----------|
| workflow | Multi-step processes with approval checkpoints | bug-fixer |
| utility | Simple lookup or single-action tools | generate-url |
| reference | Documentation-heavy lookup skills | sql-query |
| automation | Command execution and server management | server-management |

### Directory Structure Created

```
.claude/skills/{skill-name}/
    skill.md   (main skill file)
```

### Example

**User:** `/skill-manager create api-validator`

**Actions:**
1. Check `.claude/skills/api-validator/` does not exist
2. Ask: "Which template should I use for the api-validator skill?"
3. User selects "utility"
4. Create directory and skill.md from utility template
5. Report: "Created skill at `.claude/skills/api-validator/skill.md`"

---

## MODE: REVIEW

Validate a skill file for quality, completeness, and adherence to standards.

### Workflow

1. **Parse skill name** from the command argument (or "all")
2. **Read skill file** - `.claude/skills/{name}/skill.md`
3. **Run validation checks** - categorized as BLOCKING, WARNING, INFO
4. **Report results** - show pass/fail status with details

### For "review all"

1. **Find all skills** using Glob: `.claude/skills/*/skill.md`
2. **Run validation** on each skill
3. **Summarize results** - count of pass/warn/fail

### Validation Categories

**BLOCKING Issues (must fix before use):**
- [ ] YAML frontmatter exists (starts with `---`)
- [ ] `name:` field is present in frontmatter
- [ ] `description:` field is present and 20+ characters
- [ ] At least one markdown heading (`#` or `##`)
- [ ] File is named `skill.md` (case-insensitive)

**WARNING Issues (should fix):**
- [ ] Missing "Triggers" or trigger keywords in description
- [ ] Missing workflow/steps section
- [ ] No version info at end of file
- [ ] No examples section
- [ ] Description over 300 characters (too long for skill list)

**INFO Issues (suggestions):**
- [ ] Missing quick reference table
- [ ] No error handling section
- [ ] Less than 50 lines (may be too minimal)

### Report Format

```
## Skill Review: {skill-name}

### Status: PASS / WARN / FAIL

### BLOCKING Issues
- None / [list of issues]

### WARNING Issues
- None / [list of issues]

### INFO Suggestions
- None / [list of suggestions]

### Summary
{skill-name} is ready for use / needs attention.
```

---

## MODE: EDIT

Edit an existing skill with a guided workflow.

### Workflow

1. **Parse skill name** from the command argument
2. **Read current skill** - `.claude/skills/{name}/skill.md`
3. **Present current content** - show frontmatter and structure overview
4. **Ask what to edit** - specific section or free-form changes
5. **Make changes** - use Edit tool to modify
6. **Run review** - validate the changes
7. **Report result** - confirm edits were saved

### Edit Options

Present these options to the user:

| Option | Action |
|--------|--------|
| Description | Update the YAML description field |
| Triggers | Add/modify trigger keywords |
| Workflow | Update the workflow steps |
| Examples | Add or modify examples |
| Version | Update version number and changelog |
| Custom | Make specific edits by instruction |

### Example

**User:** `/skill-manager edit commit`

**Actions:**
1. Read `.claude/skills/commit/skill.md`
2. Show: "Current description: 'Invoke the commit skill to create a commit.'"
3. Ask: "What would you like to edit?"
4. User selects "Description"
5. Ask: "What should the new description be?"
6. Update using Edit tool
7. Run validation
8. Report: "Updated commit skill description. Validation: PASS"

---

## MODE: LIST

List all skills with their descriptions.

### Workflow

1. **Find all skills** using Glob: `.claude/skills/*/skill.md`
2. **Read each skill** - extract name and description from frontmatter
3. **Present results** - formatted table

### Basic List Output

```
## Skills ({count} total)

| Skill | Description |
|-------|-------------|
| bug-fixer | Fix GitHub issues by analyzing... |
| commit | Create a git commit following... |
...
```

### Categorized List (--categorize)

Group skills by inferred category based on keywords:

| Category | Keywords in name/description |
|----------|------------------------------|
| Development | bug, fix, code, warning, commit, dead-code |
| Testing | test, ui-test, qa, review |
| GitHub | issue, pr, pull-request, gh |
| Documentation | docs, readme, report |
| Infrastructure | server, build, release |
| Other | everything else |

```
## Skills by Category

### Development (5)
| Skill | Description |
...

### Testing (3)
| Skill | Description |
...
```

---

## TEMPLATES

### Template: workflow

For multi-step processes with approval checkpoints.

```markdown
---
name: {skill-name}
description: {Short description}. Triggers on "{trigger1}", "{trigger2}".
---

# {Skill Title}

{Brief overview of what this skill does}

## Quick Reference

| Action | Description |
|--------|-------------|
| Step 1 | {description} |
| Step 2 | {description} |

## CRITICAL: User Approval Required

**NEVER proceed without explicit user approval.**

## Workflow

### Step 1: {Step Name}

{Description of what this step does}

### Step 2: {Step Name}

{Description of what this step does}

### Step 3: Report Completion

Report to user:
```
## {Completion Title}

- Result: {summary}
- Files changed: {count}
- Status: SUCCESS
```

## Examples

**User:** {example input}

**Agent:**
1. {action 1}
2. {action 2}
3. {result}

---

**Skill Version:** 1.0
**Last Updated:** {date}
```

### Template: utility

For simple lookup or single-action tools.

```markdown
---
name: {skill-name}
description: {Short description}. Triggers on "{trigger1}", "{trigger2}".
---

# {Skill Title}

{Brief overview of what this skill does}

## CRITICAL: NO GUESSING

**NEVER generate output that is not documented in this skill.**

If the user asks for something not documented:
1. Check the reference code/documentation
2. If not found, respond: "This is not supported."

## Reference Table

| Type | Value |
|------|-------|
| {item1} | {value1} |
| {item2} | {value2} |

## Examples

### Example 1
```
{example input and output}
```

## Reference Code

- **Source file:** `{path/to/source.cs}`
- **Documentation:** `{path/to/docs.md}`

---

**Skill Version:** 1.0
**Last Updated:** {date}
```

### Template: reference

For documentation-heavy lookup skills.

```markdown
---
name: {skill-name}
description: {Short description}. Triggers on "{trigger1}", "{trigger2}".
---

# {Skill Title}

This skill helps you {action description}.

## Connection/Configuration

### {Config Section 1}
- Setting: `{value}`
- Notes: {notes}

### {Config Section 2}
- Setting: `{value}`
- Notes: {notes}

## Usage

### Basic Syntax

```bash
{command example}
```

### Common Options

| Option | Description |
|--------|-------------|
| `-a` | {description} |
| `-b` | {description} |

## Best Practices

1. {practice 1}
2. {practice 2}

## Workflow

When the user asks to {action}:

1. **Determine target**: {question}
2. **Gather parameters**: {what to collect}
3. **Execute**: {command}
4. **Present results**: {format}

## Error Handling

If {tool} fails:
- Check {item 1}
- Verify {item 2}

---

**Skill Version:** 1.0
**Last Updated:** {date}
```

### Template: automation

For command execution and server management.

```markdown
---
name: {skill-name}
description: {Short description}. Triggers on "{trigger1}", "{trigger2}".
---

# {Skill Title}

{Brief overview of what this skill manages}

## Quick Reference

| Action | Command |
|--------|---------|
| {action1} | `{command1}` |
| {action2} | `{command2}` |
| Check Status | `{status_command}` |

---

## Options

| Option | Description |
|--------|-------------|
| `--option1` | {description} |
| `--option2` | {description} |

---

## {Action 1}

```bash
{command}
```

{Description of what this does}

**Bash tool parameters:** {timeout or run_in_background settings}

---

## {Action 2}

```bash
{command}
```

{Description of what this does}

---

## Configurations

| Item | Value |
|------|-------|
| {config1} | {value1} |
| {config2} | {value2} |

---

## Workflow Examples

### {Scenario 1}

```bash
{commands}
```

### {Scenario 2}

```bash
{commands}
```

---

## Troubleshooting

### {Issue 1}
- {Solution}

### {Issue 2}
- {Solution}

---

**Skill Version:** 1.0
**Last Updated:** {date}
**Script/Source:** `{path/to/script}`
```

---

## Validation Rules Reference

### Frontmatter Requirements

The YAML frontmatter must contain:

```yaml
---
name: skill-name
description: Clear description with trigger keywords. At least 20 characters.
---
```

### Good Description Examples

**Good:**
- "Fix GitHub issues by analyzing the issue... Triggers on 'fix bug', 'bug #'."
- "Execute SQL Server queries on localhost. Use when querying databases, running SQL."
- "Start and stop servers for testing and development."

**Bad:**
- "A skill for stuff" (too vague, no triggers)
- "skill" (too short)
- Very long description over 300 characters (gets truncated in skill lists)

### Structure Requirements

A well-structured skill should have:

1. **Title heading** (`# Skill Name`)
2. **Quick reference** (table of main actions)
3. **Workflow or usage section** (how to use)
4. **Examples** (at least one)
5. **Version footer** (version number, date)

---

## Error Handling

### Skill Not Found

If the requested skill does not exist:
```
Skill '{name}' not found.

Available skills: {list first 10}

Did you mean one of these?
```

### Invalid Mode

If the mode is not recognized:
```
Unknown mode. Use one of:
- /skill-manager create <name>
- /skill-manager review <name|all>
- /skill-manager edit <name>
- /skill-manager list [--categorize]
```

### Create Conflict

If skill already exists during create:
```
Skill '{name}' already exists at:
.claude/skills/{name}/skill.md

Use '/skill-manager edit {name}' to modify it.
```

---

## Implementation Notes

This skill uses only Claude Code tools - no external scripts:

| Tool | Usage |
|------|-------|
| Glob | Find skill files (`.claude/skills/*/skill.md`) |
| Read | Read skill content for review/edit/list |
| Write | Create new skill files |
| Edit | Modify existing skill files |

### Skills Directory

Skills are located at: `.claude/skills/`

Each skill is a directory containing at minimum a `skill.md` file.

---

**Skill Version:** 1.0
**Last Updated:** 2025-02-14
