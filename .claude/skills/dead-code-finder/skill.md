---
name: dead-code-finder
description: Find dead code and unused files/components in CC Director. Use when you need to identify unused projects, classes, or services that can be safely removed to simplify the codebase.
---

# Dead Code Finder Skill

Find and identify dead code in the CC Director application to simplify maintenance and reduce codebase complexity.

## Scope

**INCLUDED directories:**
- `src/CcDirector.Core/` - Core business logic
- `src/CcDirector.Wpf/` - WPF application
- `src/CcClick/` - CLI tool for UI automation

**EXCLUDED directories:**
- `src/CcDirector.Core.Tests/` - Test projects (don't flag as dead code)
- `src/CcDirector.TestHarness/` - Test utilities

## Analysis Types

### 1. Unused Project Detection

Find projects (.csproj) that are:
- Not referenced by any other project in the solution
- Not an entry point (WPF app, console app)
- Only referenced by other unused projects (chain detection)

### 2. Unused File Detection

Find C# files (.cs) where:
- All public classes/interfaces have zero external references
- File is not an entry point (Program.cs, App.xaml.cs)
- File is not a test file

### 3. Unused XAML Detection

Find XAML files (.xaml) where:
- UserControl or Window is never instantiated
- Not referenced in any other XAML file
- Not the main window (MainWindow.xaml)

### 4. Unused Service Detection

Find services where:
- Registered in DI (if applicable)
- But never injected or used

## Manual Analysis Steps

### Step 1: Find Unused Projects

```bash
# List all project references in solution
grep -r "ProjectReference" --include="*.csproj" src/
```

Compare against project list to find unreferenced projects.

### Step 2: Find Unused Classes

```bash
# Find public classes
grep -r "public class " --include="*.cs" src/ | grep -v Tests

# For each class, search for references
grep -r "ClassName" --include="*.cs" --include="*.xaml" src/
```

### Step 3: Find Unused XAML Components

```bash
# Find all XAML files
find src -name "*.xaml" -not -name "App.xaml" -not -name "*.Designer.xaml"

# For each UserControl, search for usage
grep -r "<local:ControlName" --include="*.xaml" src/
```

### Step 4: Find Unused Methods

Use IDE tools or:

```bash
# Find public methods
grep -r "public.*\w\+\s*(" --include="*.cs" src/ | grep -v Tests

# Search for method name usage
grep -r "MethodName" --include="*.cs" src/
```

## Output Format

The analysis produces a structured report:

```
================================================================================
DEAD CODE ANALYSIS REPORT
Generated: {timestamp}
================================================================================

SUMMARY
-------
Potentially unused projects: X
Potentially unused files: Y
Potentially unused controls: Z

================================================================================
SECTION 1: UNUSED PROJECTS (High Confidence)
================================================================================

Project: src/CcDirector.OldFeature/CcDirector.OldFeature.csproj
Status: NOT REFERENCED
Reason: No ProjectReference found in any other .csproj
Action: Review and delete if confirmed unused

---

================================================================================
SECTION 2: UNUSED FILES (High Confidence)
================================================================================

File: src/CcDirector.Core/Legacy/OldHelper.cs
Classes: OldHelper
References found: 0
Action: Review and delete if confirmed unused

---

================================================================================
SECTION 3: UNUSED XAML CONTROLS (High Confidence)
================================================================================

Control: OldDialog.xaml
Location: src/CcDirector.Wpf/Dialogs/
Usage search: <local:OldDialog found 0 times
Action: Review and delete if confirmed unused

---
```

## Safety Guidelines

1. **Never auto-delete** - Always review before removing code
2. **Check git history** - Some code may be recently added and not yet integrated
3. **Search for string references** - Some code is loaded dynamically by name
4. **Check configuration files** - Services may be registered in config
5. **Build after removal** - Always run `dotnet build cc_director.sln` after removing code

## Removal Workflow

When you've confirmed code is dead:

1. **Document the removal** in the commit message
2. **Remove in order**: Files first, then projects
3. **Update solution file** if removing projects
4. **Build the solution** to verify nothing breaks
5. **Run tests** if applicable

```bash
# Remove project from solution
dotnet sln cc_director.sln remove "src/path/to/project.csproj"

# Delete the project directory
rm -rf "src/path/to/project"

# Build to verify
dotnet build cc_director.sln

# Run tests
dotnet test src/CcDirector.Core.Tests
```

## Common False Positives

**Entry Points** - These are NEVER dead code:
- `CcDirector.Wpf` - Main WPF app
- `CcClick` - CLI tool
- `CcDirector.TestHarness` - Test utility

**Infrastructure Code:**
- `App.xaml` / `App.xaml.cs`
- `AssemblyInfo.cs`
- Extension methods classes
- Attribute classes

**XAML Resources:**
- Styles defined in ResourceDictionaries
- Converters used via StaticResource

---

**Skill Version:** 1.0
**Last Updated:** 2026-02-14
**Adapted from:** mindzieWeb dead-code-finder skill
