# Claude Agent SDK (formerly Claude Code SDK)

> Official documentation: https://platform.claude.com/docs/en/agent-sdk/overview

Build AI agents that autonomously read files, run commands, search the web, edit code, and more. The Agent SDK gives you the same tools, agent loop, and context management that power Claude Code, programmable in Python and TypeScript.

## Installation

**Python:**
```bash
pip install claude-agent-sdk
```

**TypeScript:**
```bash
npm install @anthropic-ai/claude-agent-sdk
```

The Claude Code CLI is automatically bundled with the package - no separate installation required.

## Authentication

Set your API key:
```bash
export ANTHROPIC_API_KEY=your-api-key
```

Also supports:
- Amazon Bedrock: `CLAUDE_CODE_USE_BEDROCK=1`
- Google Vertex AI: `CLAUDE_CODE_USE_VERTEX=1`
- Microsoft Azure: `CLAUDE_CODE_USE_FOUNDRY=1`

## Basic Usage

### Python

```python
import asyncio
from claude_agent_sdk import query, ClaudeAgentOptions

async def main():
    async for message in query(
        prompt="Find and fix the bug in auth.py",
        options=ClaudeAgentOptions(allowed_tools=["Read", "Edit", "Bash"])
    ):
        print(message)

asyncio.run(main())
```

### TypeScript

```typescript
import { query } from "@anthropic-ai/claude-agent-sdk";

for await (const message of query({
  prompt: "Find and fix the bug in auth.py",
  options: { allowedTools: ["Read", "Edit", "Bash"] }
})) {
  console.log(message);
}
```

## Built-in Tools

| Tool | What it does |
|------|--------------|
| **Read** | Read any file in the working directory |
| **Write** | Create new files |
| **Edit** | Make precise edits to existing files |
| **Bash** | Run terminal commands, scripts, git operations |
| **Glob** | Find files by pattern (`**/*.ts`, `src/**/*.py`) |
| **Grep** | Search file contents with regex |
| **WebSearch** | Search the web for current information |
| **WebFetch** | Fetch and parse web page content |
| **AskUserQuestion** | Ask the user clarifying questions |
| **Task** | Spawn subagents for focused tasks |

## Key Capabilities

### 1. Hooks

Run custom code at key points in the agent lifecycle:

```python
async def log_file_change(input_data, tool_use_id, context):
    file_path = input_data.get('tool_input', {}).get('file_path', 'unknown')
    with open('./audit.log', 'a') as f:
        f.write(f"{datetime.now()}: modified {file_path}\n")
    return {}

async for message in query(
    prompt="Refactor utils.py",
    options=ClaudeAgentOptions(
        permission_mode="acceptEdits",
        hooks={
            "PostToolUse": [HookMatcher(matcher="Edit|Write", hooks=[log_file_change])]
        }
    )
):
    print(message)
```

Available hooks: `PreToolUse`, `PostToolUse`, `Stop`, `SessionStart`, `SessionEnd`, `UserPromptSubmit`

### 2. Subagents

Spawn specialized agents for focused subtasks:

```python
async for message in query(
    prompt="Use the code-reviewer agent to review this codebase",
    options=ClaudeAgentOptions(
        allowed_tools=["Read", "Glob", "Grep", "Task"],
        agents={
            "code-reviewer": AgentDefinition(
                description="Expert code reviewer for quality and security reviews.",
                prompt="Analyze code quality and suggest improvements.",
                tools=["Read", "Glob", "Grep"]
            )
        }
    )
):
    print(message)
```

### 3. MCP (Model Context Protocol)

Connect to external systems - databases, browsers, APIs:

```python
async for message in query(
    prompt="Open example.com and describe what you see",
    options=ClaudeAgentOptions(
        mcp_servers={
            "playwright": {"command": "npx", "args": ["@playwright/mcp@latest"]}
        }
    )
):
    print(message)
```

### 4. Permissions

Control exactly which tools your agent can use:

```python
# Read-only agent - can analyze but not modify
async for message in query(
    prompt="Review this code for best practices",
    options=ClaudeAgentOptions(
        allowed_tools=["Read", "Glob", "Grep"],
        permission_mode="bypassPermissions"
    )
):
    print(message)
```

### 5. Session Management

Maintain context across multiple exchanges:

```python
session_id = None

# First query: capture the session ID
async for message in query(
    prompt="Read the authentication module",
    options=ClaudeAgentOptions(allowed_tools=["Read", "Glob"])
):
    if hasattr(message, 'subtype') and message.subtype == 'init':
        session_id = message.session_id

# Resume with full context
async for message in query(
    prompt="Now find all places that call it",
    options=ClaudeAgentOptions(resume=session_id)
):
    print(message)
```

Sessions are saved to disk (`~/.claude/projects/`) and can be resumed later.

## Can You Communicate With a Running claude.exe?

**Short answer: Not via direct IPC (Inter-Process Communication).**

The SDK does NOT connect to an already-running `claude.exe` process. Instead, it:

1. **Spawns its own process** - Each `query()` call starts a new Claude agent process
2. **Uses disk-based session persistence** - Sessions are saved to `~/.claude/projects/`
3. **Resumes via session ID** - You can resume previous sessions by passing the session ID

### What This Means

- You cannot "attach" to a running Claude Code terminal session programmatically
- You CAN resume sessions that were started elsewhere (CLI or SDK) using their session ID
- The SDK and CLI share the same session storage, so they can resume each other's sessions

### CLI Session Commands

```bash
# Continue most recent conversation
claude -c
claude --continue

# Resume specific session by ID
claude -r "abc123"
claude --resume abc123

# Interactive session picker
claude --resume
```

### SDK Session Resume

```python
# Resume a session from CLI or another SDK instance
async for message in query(
    prompt="Continue where we left off",
    options=ClaudeAgentOptions(resume="session-id-from-cli")
):
    print(message)
```

## SDK vs CLI vs Client SDK

| Feature | Agent SDK | Claude Code CLI | Client SDK |
|---------|-----------|-----------------|------------|
| Tool execution | Built-in | Built-in | You implement |
| Use case | CI/CD, automation | Interactive dev | Direct API access |
| Programmatic | Yes | Limited (`-p` flag) | Yes |
| Production ready | Yes | No | Yes |

## Use Cases

1. **CI/CD Pipelines** - Automated code review, bug fixes, test generation
2. **Custom Applications** - Build your own AI-powered dev tools
3. **Production Automation** - Scheduled tasks, batch processing
4. **Code Review Bots** - Automated PR review with custom rules
5. **Documentation Generation** - Auto-generate docs from code
6. **Migration Scripts** - Automated codebase migrations

## Resources

- **Official Docs**: https://platform.claude.com/docs/en/agent-sdk/overview
- **Python SDK GitHub**: https://github.com/anthropics/claude-agent-sdk-python
- **TypeScript SDK GitHub**: https://github.com/anthropics/claude-agent-sdk-typescript
- **Example Agents**: https://github.com/anthropics/claude-agent-sdk-demos
- **NPM Package**: https://www.npmjs.com/package/@anthropic-ai/claude-agent-sdk
