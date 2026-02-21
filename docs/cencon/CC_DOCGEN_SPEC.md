# cc_docgen - Architecture Diagram Generator

**Version:** 1.0.0
**Status:** Specification (not yet implemented)
**Author:** CenCon Method

---

## Overview

`cc_docgen` is a CLI tool that reads `architecture_manifest.yaml` and generates C4 model diagrams as PNG images. It automates the creation of context and container diagrams from the machine-readable manifest.

---

## Command Interface

```bash
cc_docgen generate [OPTIONS]

Options:
  --manifest PATH    Path to architecture_manifest.yaml (default: ./docs/cencon/architecture_manifest.yaml)
  --output DIR       Output directory for diagrams (default: ./docs/cencon/)
  --format FORMAT    Output format: png, svg (default: png)
  --theme THEME      Diagram theme: default, dark, light (default: default)
  --verbose          Enable verbose output
  --help             Show help message
```

### Examples

```bash
# Generate diagrams with defaults
cc_docgen generate

# Specify manifest and output
cc_docgen generate --manifest ./docs/cencon/architecture_manifest.yaml --output ./docs/cencon/

# Generate SVG format
cc_docgen generate --format svg

# Verbose mode for debugging
cc_docgen generate --verbose
```

---

## Output Files

| File | Description | C4 Level |
|------|-------------|----------|
| `context.png` | System context diagram showing actors and external systems | Level 1 |
| `container.png` | Container diagram showing internal application structure | Level 2 |

---

## Implementation Options

### Option 1: Python + diagrams library (Recommended)

**Pros:**
- Clean Python API for diagram generation
- Built-in C4 model support
- Easy to maintain and extend
- Cross-platform

**Cons:**
- Requires Graphviz installed
- Python environment needed

**Dependencies:**
```
diagrams>=0.23.0
pyyaml>=6.0
graphviz (system package)
```

**Sample Implementation:**

```python
from diagrams import Diagram, Cluster
from diagrams.c4 import Person, System, Container, SystemBoundary
import yaml

def generate_context_diagram(manifest: dict, output_path: str):
    """Generate C4 Level 1 Context diagram."""
    with Diagram(
        "CC Director - System Context",
        filename=output_path,
        show=False,
        direction="TB"
    ):
        # Create actors
        developer = Person("Developer")

        # Create main system
        cc_director = System("CC Director", "Multi-session Claude Code management")

        # Create external systems
        claude_code = System("Claude Code CLI", "Anthropic CLI", external=True)
        git = System("Git", "Version control", external=True)
        openai = System("OpenAI API", "Speech services", external=True)

        # Define relationships
        developer >> cc_director
        cc_director >> claude_code
        cc_director >> git
        cc_director >> openai

def generate_container_diagram(manifest: dict, output_path: str):
    """Generate C4 Level 2 Container diagram."""
    with Diagram(
        "CC Director - Container Diagram",
        filename=output_path,
        show=False,
        direction="TB"
    ):
        developer = Person("Developer")

        with SystemBoundary("CC Director"):
            wpf_ui = Container("WPF UI Layer", "C# / WPF", "Desktop user interface")
            core = Container("Core Services", "C# / .NET 10", "Business logic")
            native = Container("Native APIs", "Win32 / P/Invoke", "OS integration")

        # External
        claude = System("Claude Code", external=True)

        developer >> wpf_ui
        wpf_ui >> core
        core >> native
        core >> claude
```

### Option 2: PlantUML

**Pros:**
- Text-based diagram definition
- Wide tool support
- No Python needed (Java-based)

**Cons:**
- Requires Java runtime
- More verbose syntax
- Less programmatic control

**Sample PlantUML:**
```plantuml
@startuml
!include C4_Context.puml

Person(developer, "Developer")
System(cc_director, "CC Director", "Multi-session management")
System_Ext(claude, "Claude Code CLI", "Anthropic CLI")

Rel(developer, cc_director, "Uses")
Rel(cc_director, claude, "Spawns and monitors")
@enduml
```

### Option 3: Mermaid

**Pros:**
- JavaScript-based, runs in browser
- GitHub native rendering
- No external dependencies

**Cons:**
- Limited C4 model support
- Less professional appearance
- Output quality varies

---

## Manifest Schema Requirements

The tool expects `architecture_manifest.yaml` to follow this schema:

```yaml
schema_version: "1.0.0"

project:
  name: string          # Required
  description: string   # Required
  version: string       # Required

context:
  system:
    name: string        # Required
    description: string # Required
    technology: string  # Required

  actors:
    - id: string        # Required, unique
      name: string      # Required
      description: string
      type: person | external_system
      relationship:
        target: string  # System ID
        description: string

containers:
  - id: string          # Required, unique
    name: string        # Required
    technology: string  # Required
    description: string
    project: string     # Visual Studio project name
    components:
      - name: string
        description: string
        file: string | files: [string]
```

---

## Error Handling

| Error | Exit Code | Message |
|-------|-----------|---------|
| Manifest not found | 1 | `ERROR: Manifest file not found: {path}` |
| Invalid YAML | 2 | `ERROR: Invalid YAML syntax: {details}` |
| Missing required field | 3 | `ERROR: Missing required field: {field}` |
| Graphviz not installed | 4 | `ERROR: Graphviz not installed. Install with: choco install graphviz` |
| Output directory not writable | 5 | `ERROR: Cannot write to output directory: {path}` |

---

## Integration Notes

### When to Run cc_docgen

1. **After modifying architecture_manifest.yaml** - Regenerate diagrams
2. **During CI/CD** - Ensure diagrams are current
3. **Before documentation release** - Verify diagrams exist

### Integration with /review-code

The `/review-code` skill checks if diagrams exist. If `context.png` or `container.png` are missing but `architecture_manifest.yaml` exists, it will add a SUGGESTION:

```
SUGGESTION: Diagrams missing. Run 'cc_docgen generate' to create them.
```

### CI/CD Integration Example

```yaml
# GitHub Actions example
- name: Generate architecture diagrams
  run: |
    pip install diagrams pyyaml
    cc_docgen generate --manifest docs/cencon/architecture_manifest.yaml

- name: Verify diagrams generated
  run: |
    test -f docs/cencon/context.png
    test -f docs/cencon/container.png
```

---

## Installation

### Prerequisites

1. Python 3.10+
2. Graphviz (`choco install graphviz` on Windows)

### Install Tool

```bash
pip install cc_docgen
```

Or from source:

```bash
cd cc_tools/src/cc_docgen
pip install -e .
```

---

## Project Structure (When Implemented)

```
cc_tools/src/cc_docgen/
    __init__.py
    cli.py              # Click CLI entry point
    generator.py        # Diagram generation logic
    schema.py           # YAML schema validation
    templates/          # Diagram templates
        context.py
        container.py
    tests/
        test_generator.py
        fixtures/
            sample_manifest.yaml
```

---

## Future Enhancements

1. **Component diagrams (C4 Level 3)** - Detailed component breakdown
2. **Interactive HTML output** - Clickable diagram elements
3. **Diff visualization** - Show changes between versions
4. **Mermaid fallback** - Generate Mermaid for GitHub rendering when Graphviz unavailable

---

*Specification for cc_docgen v1.0 - Implementation by separate agent*
