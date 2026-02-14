---
name: warning-fixer
description: Fix compiler warnings using proper null handling and senior developer practices. Use when asked to fix warnings in a file, clean up nullable warnings, or resolve CS warnings. Works one file at a time with test coverage.
---

# Warning Fixer Skill

Fix C# compiler warnings properly - using guard clauses and validation, NOT the `!` operator.

## Why This Skill Exists

The `!` (null-forgiving operator) is a common but **wrong** way to handle nullable warnings. It tells the compiler "trust me, this won't be null" - but if it IS null at runtime, you get a NullReferenceException. Using `!` hides the warning without fixing the actual problem.

A senior developer fixes warnings properly by:
- Understanding WHY the compiler is warning
- Adding proper validation (guard clauses, null checks)
- Making the code actually safe, not just quiet

## Core Principles (Non-Negotiable)

1. **NEVER use `!` (null-forgiving operator)** - This hides warnings, doesn't fix them
2. **One file at a time** - Methodical, reviewable changes
3. **One warning type at a time** - Focused fixes
4. **Test first** - Add/verify test cases before modifying code
5. **Don't change process flow** - Conservative, behavior-preserving changes
6. **User approval required** - No changes without explicit approval
7. **Understand before fixing** - Read and comprehend the code first

## Quick Reference - Warning Codes

| Code | Description | Proper Fix |
|------|-------------|------------|
| CS8602 | Dereference of possibly null | Add null check or guard clause |
| CS8604 | Possible null reference argument | Validate before calling, throw if invalid |
| CS8600 | Converting null to non-nullable | Guard clause or make type nullable |
| CS8601 | Possible null reference assignment | Initialize properly or add validation |
| CS8625 | Cannot convert null literal | Use proper initialization |
| CS0219 | Variable never used | Remove the variable |

## Workflow

### Step 1: Identify Target File

User specifies file OR skill finds next file with warnings:

```bash
# Build and capture warnings for specific file
dotnet build cc_director.sln 2>&1 | grep "path/to/file.cs"

# Or find files with most warnings
dotnet build cc_director.sln 2>&1 | grep "warning CS" | cut -d'(' -f1 | sort | uniq -c | sort -rn | head -20
```

### Step 2: Read and Understand the File

**CRITICAL:** Read the entire file before proposing fixes. Understand:
- What the class/method does
- The data flow
- Why values might be null

### Step 3: Categorize Warnings

Group warnings by code and pick ONE type to fix first:

```bash
# Count warnings by type in specific file
dotnet build cc_director.sln 2>&1 | grep "path/to/file.cs" | grep -oP "warning CS\d+" | sort | uniq -c | sort -rn
```

### Step 4: Analyze Test Coverage

- Find existing tests for the file
- Identify what behavior is covered
- For test files: the file IS the test coverage

### Step 5: Present Fix Plan (APPROVAL CHECKPOINT)

Show the user:
- Which warnings are being fixed
- The proposed changes (BAD vs GOOD pattern)
- WHY this is the correct fix

**Wait for explicit approval before making any changes.**

### Step 6: Implement Fix

After approval:
1. Make the changes
2. Build the solution: `dotnet build cc_director.sln`
3. Run tests: `dotnet test src/CcDirector.Tests`

### Step 7: Verify

- Confirm warnings are gone
- Confirm tests still pass
- Report results to user

## Fix Patterns

### CS8602/CS8604 - Null Reference Warnings

**BAD - Hiding with `!`:**
```csharp
DoSomething(parameter!);
var value = obj.Property!.Value;
```

**GOOD - For required inputs:**
```csharp
public void Execute(string input)
{
    if (string.IsNullOrEmpty(input))
        throw new ArgumentException("Input is required", nameof(input));

    // Compiler now knows input is not null
    Process(input);
}
```

**GOOD - For optional inputs:**
```csharp
// Use null coalescing with sensible default
var result = optionalValue ?? "default";

// Or early return
if (optionalValue == null)
    return;
```

**GOOD - For properties that must be set:**
```csharp
public void Execute()
{
    if (string.IsNullOrEmpty(Name))
        throw new InvalidOperationException($"{nameof(Name)} must be set before calling Execute");

    // Now safe to use Name
}
```

### CS8600/CS8601 - Null Assignment Warnings

**BAD:**
```csharp
string value = GetPossiblyNull()!;
```

**GOOD - Make nullable if it can be null:**
```csharp
string? value = GetPossiblyNull();
if (value == null) return;
```

**GOOD - Validate and throw:**
```csharp
string value = GetPossiblyNull()
    ?? throw new InvalidOperationException("Value was unexpectedly null");
```

### CS0219 - Unused Variable

**BAD:**
```csharp
var unused = SomeMethod();
```

**GOOD - Remove if truly unused:**
```csharp
SomeMethod();
```

**GOOD - Use discard if return value intentionally ignored:**
```csharp
_ = SomeMethod();
```

### Test Code Specific Patterns

For test assertions, add explicit null checks:

**BAD:**
```csharp
Assert.Equal(expected, result!.Data!.Value);
```

**GOOD:**
```csharp
Assert.NotNull(result);
Assert.NotNull(result.Data);
Assert.Equal(expected, result.Data.Value);
```

## Commands Reference

```bash
# Build full solution
dotnet build cc_director.sln

# Build specific project to see its warnings
dotnet build src/CcDirector.Core/CcDirector.Core.csproj --no-incremental

# Run tests for specific project
dotnet test src/CcDirector.Tests

# Count warnings by type in solution
dotnet build cc_director.sln 2>&1 | grep -oP "warning CS\d+" | sort | uniq -c | sort -rn

# Find files with most warnings
dotnet build cc_director.sln 2>&1 | grep "warning CS" | cut -d'(' -f1 | sort | uniq -c | sort -rn | head -20
```

## Progress Tracking

Use TodoWrite to track:
- Which file is being fixed
- Which warning type is being addressed
- Test status (written/passing)
- Fix status (planned/approved/implemented/verified)

## Example Session

```
User: /warning-fixer SessionManager.cs

Claude:
## Warning Analysis - SessionManager.cs

**Warnings Found:** 8
| Code | Count | Description |
|------|-------|-------------|
| CS8602 | 6 | Dereference of possibly null reference |
| CS8604 | 2 | Possible null reference argument |

**Test Coverage:** src/CcDirector.Tests/SessionManagerTests.cs

**Recommendation:** Fix CS8602 warnings first (most common)

### CS8602 Pattern Analysis

Line 48: `session.Buffer.DumpAll()`
- `Buffer` could be null
- Current code assumes it's populated

**Proposed Fix:**
```csharp
// BEFORE
var data = session.Buffer.DumpAll();

// AFTER
if (session.Buffer is null)
    throw new InvalidOperationException($"Session {session.Id} has no buffer");
var data = session.Buffer.DumpAll();
```

**Approve this fix pattern?** Reply "yes" to proceed.
```

---

**Skill Version:** 1.0
**Last Updated:** 2026-02-14
**Adapted from:** mindzieWeb warning-fixer skill
**Key Principle:** Fix warnings properly, never hide them with `!`
