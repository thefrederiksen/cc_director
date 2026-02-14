# Product Requirements Document: CC Director Remote Control

## Overview

**Feature Name:** CC Director Remote Control
**Version:** 1.0
**Date:** February 12, 2026
**Status:** Draft

### Executive Summary

CC Director Remote Control enables users to monitor and control their Claude Code sessions from any device with a web browser. This feature extends CC Director's capabilities beyond the desktop, allowing users to check on long-running tasks, respond to prompts, and manage sessions from their phone or tablet.

### Business Model

- **CC Director** remains **open source** and free
- **Remote Control hosting service** is a **paid subscription** (not open source)
- Revenue model: Monthly/annual subscription for cloud connectivity

---

## Problem Statement

### Current Limitations

1. Users must be physically at their computer to interact with Claude Code sessions
2. Long-running tasks cannot be monitored remotely
3. Sessions requiring user input block until the user returns to their desk
4. No way to check session status while away from the computer

### User Stories

1. **As a developer**, I want to check on my Claude Code session from my phone while getting coffee, so I can see if it needs input or has completed.

2. **As a remote worker**, I want to respond to Claude's questions from my tablet while in a meeting, so my session doesn't sit idle waiting for me.

3. **As a team lead**, I want to monitor multiple sessions across different machines, so I can track progress on various tasks.

---

## Solution Architecture

### High-Level Design

```
+------------------+        WebSocket        +------------------+
|  CC Director     | <--------------------> |  Cloud Service   |
|  (Windows App)   |    (Persistent)        |  (Vercel)        |
+------------------+                        +------------------+
                                                    |
                                                    | REST/WebSocket
                                                    v
                                            +------------------+
                                            |  Web Client      |
                                            |  (Browser)       |
                                            +------------------+
                                                    |
                                                    v
                                            +------------------+
                                            |  Supabase        |
                                            |  (Database)      |
                                            +------------------+
```

### Technology Stack

| Component | Technology | Rationale |
|-----------|------------|-----------|
| Cloud Hosting | Vercel | Serverless, auto-scaling, edge functions |
| Database | Supabase | PostgreSQL with real-time subscriptions, built-in auth |
| Authentication | Supabase Auth (GitHub OAuth) | Simple setup, fits developer audience |
| Real-time Communication | WebSocket | Persistent connection, low latency |
| Web Framework | Next.js | Works natively with Vercel, React-based |

---

## Functional Requirements

### FR-1: Authentication

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-1.1 | Users authenticate via GitHub OAuth through Supabase Auth | Must Have |
| FR-1.2 | Same GitHub account used for both web and CC Director login | Must Have |
| FR-1.3 | CC Director provides a registration form to link the computer to the user's account | Must Have |
| FR-1.4 | Users can register multiple computers under one account | Must Have |
| FR-1.5 | Users can name/rename their registered computers | Should Have |
| FR-1.6 | Users can unregister computers from their account | Must Have |

### FR-2: Connection Management

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-2.1 | CC Director maintains persistent WebSocket connection to cloud service | Must Have |
| FR-2.2 | Automatic reconnection on connection loss with exponential backoff | Must Have |
| FR-2.3 | Connection status indicator in CC Director UI | Must Have |
| FR-2.4 | Offline mode: CC Director works fully offline, syncs when reconnected | Must Have |
| FR-2.5 | Heartbeat mechanism to detect stale connections | Must Have |

### FR-3: Terminal Streaming

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-3.1 | Stream terminal character data from CC Director to cloud in real-time | Must Have |
| FR-3.2 | Store terminal session data in Supabase database | Must Have |
| FR-3.3 | Web client renders terminal identically to desktop application | Must Have |
| FR-3.4 | Support ANSI color codes and terminal formatting | Must Have |
| FR-3.5 | Handle terminal resize events and update web display accordingly | Should Have |
| FR-3.6 | Scrollback buffer synchronized between desktop and web | Should Have |

### FR-4: Remote Input

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-4.1 | Text input box on web client for sending commands | Must Have |
| FR-4.2 | Input sent from web is relayed to CC Director and executed | Must Have |
| FR-4.3 | Support for special keys (Enter, Ctrl+C, Tab, etc.) | Must Have |
| FR-4.4 | Input appears in terminal on both web and desktop simultaneously | Must Have |
| FR-4.5 | On-screen keyboard support for mobile devices | Should Have |

### FR-5: Session Management

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-5.1 | View list of all sessions across all registered computers | Must Have |
| FR-5.2 | See session status (running, waiting for input, completed) | Must Have |
| FR-5.3 | Switch between sessions on web client | Must Have |
| FR-5.4 | Session activity indicators (last activity timestamp) | Should Have |
| FR-5.5 | Start new session from web client | Could Have |
| FR-5.6 | Terminate session from web client | Should Have |

### FR-6: Web Client UI

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-6.1 | Responsive design for desktop, tablet, and phone | Must Have |
| FR-6.2 | Touch-friendly interface for mobile devices | Must Have |
| FR-6.3 | Dark theme matching CC Director desktop app | Must Have |
| FR-6.4 | Computer/session selector sidebar | Must Have |
| FR-6.5 | Connection status indicator | Must Have |
| FR-6.6 | Notification when session needs input | Should Have |

---

## Non-Functional Requirements

### NFR-1: Performance

| ID | Requirement | Target |
|----|-------------|--------|
| NFR-1.1 | Terminal update latency (desktop to web) | < 500ms |
| NFR-1.2 | Input latency (web to desktop execution) | < 500ms |
| NFR-1.3 | Initial terminal load time | < 2 seconds |
| NFR-1.4 | WebSocket reconnection time | < 5 seconds |

### NFR-2: Reliability

| ID | Requirement | Target |
|----|-------------|--------|
| NFR-2.1 | Cloud service uptime | 99.9% |
| NFR-2.2 | No data loss during brief disconnections | Required |
| NFR-2.3 | Graceful degradation when cloud unavailable | Required |

### NFR-3: Security

| ID | Requirement | Details |
|----|-------------|---------|
| NFR-3.1 | All connections encrypted via TLS | Required |
| NFR-3.2 | Authentication tokens expire and refresh | Required |
| NFR-3.3 | Computer registration requires active GitHub session | Required |
| NFR-3.4 | Rate limiting on API endpoints | Required |
| NFR-3.5 | Session data encrypted at rest in Supabase | Required |

### NFR-4: Scalability

| ID | Requirement | Target |
|----|-------------|--------|
| NFR-4.1 | Support concurrent users | 10,000+ |
| NFR-4.2 | Support sessions per user | 50+ |
| NFR-4.3 | Support computers per user | 10+ |

---

## Data Model

### Supabase Tables

#### users (Supabase Auth managed)
```
- id: UUID (PK)
- email: string
- github_id: string
- github_username: string
- created_at: timestamp
- last_login: timestamp
```

#### computers
```
- id: UUID (PK)
- user_id: UUID (FK -> users.id)
- name: string
- machine_id: string (unique hardware identifier)
- registered_at: timestamp
- last_seen: timestamp
- is_online: boolean
```

#### sessions
```
- id: UUID (PK)
- computer_id: UUID (FK -> computers.id)
- repo_path: string
- started_at: timestamp
- ended_at: timestamp (nullable)
- status: enum (running, waiting_input, completed, error)
```

#### terminal_data
```
- id: UUID (PK)
- session_id: UUID (FK -> sessions.id)
- sequence: bigint (ordering)
- data: text (terminal characters/escape sequences)
- timestamp: timestamp
```

---

## CC Director Changes

### New Components

1. **CloudConnectionManager** - Manages WebSocket connection to cloud service
2. **TerminalStreamPublisher** - Streams terminal data to cloud
3. **RemoteInputHandler** - Receives and processes input from web client
4. **RegistrationDialog** - UI for registering computer with account

### New Menu Items

- **File > Remote Control > Register Computer...**
- **File > Remote Control > Connection Status**
- **File > Remote Control > Disconnect**

### Status Bar Addition

- Cloud connection indicator (connected/disconnected/connecting)

### Settings

- Enable/disable remote control
- Auto-connect on startup
- Connection timeout settings

---

## Web Client Features

### Dashboard View

- List of registered computers with online status
- Quick stats: total sessions, active sessions
- Recent activity feed

### Session View

- Full terminal display (matching desktop rendering)
- Input text box with send button
- Session info panel (repo path, duration, status)
- Quick actions (scroll to bottom, clear, terminate)

### Mobile Optimizations

- Swipe gestures for navigation
- Collapsible sidebar
- Large touch targets
- Landscape mode for terminal viewing

---

## Implementation Phases

### Phase 1: Foundation (MVP)

**Duration:** 4-6 weeks

**Deliverables:**
1. Supabase project setup with auth and database schema
2. CC Director: GitHub OAuth login and computer registration
3. CC Director: WebSocket connection to cloud
4. Cloud service: Basic API endpoints on Vercel
5. Web client: Login, computer list, basic terminal view
6. Terminal streaming (one-way: desktop to web)

**Success Criteria:**
- User can register CC Director with their account
- User can view terminal output on web browser
- Connection is stable over extended periods

### Phase 2: Interactive

**Duration:** 3-4 weeks

**Deliverables:**
1. Remote input from web to CC Director
2. Session management (list, switch, status)
3. Mobile-responsive web interface
4. Connection reliability improvements

**Success Criteria:**
- User can send commands from phone
- Commands execute on desktop and output appears on both
- Works well on mobile browsers

### Phase 3: Polish

**Duration:** 2-3 weeks

**Deliverables:**
1. Notifications (session needs input)
2. Multi-computer management
3. Session history and search
4. Performance optimizations
5. Error handling improvements

### Phase 4: Native Apps (Future)

**Duration:** TBD

**Deliverables:**
1. Android app (React Native or native)
2. iOS app (React Native or native)
3. Push notifications
4. Background sync

---

## Risks and Mitigations

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| WebSocket connection instability | High | Medium | Implement robust reconnection, queue messages during disconnect |
| High latency on mobile networks | Medium | Medium | Optimize payload size, batch updates |
| Data sync conflicts | Medium | Low | Use sequence numbers, server is source of truth |
| Security vulnerabilities | High | Low | Security audit, penetration testing, regular updates |
| Supabase rate limits | Medium | Medium | Implement client-side throttling, upgrade plan if needed |

---

## Success Metrics

| Metric | Target | Measurement |
|--------|--------|-------------|
| User adoption | 20% of CC Director users register | Supabase user count |
| Active usage | 50% of registered users use weekly | Weekly active users |
| Reliability | < 0.1% failed message delivery | Error logs |
| Performance | 95th percentile latency < 500ms | Performance monitoring |
| User satisfaction | > 4.0 rating | User surveys |

---

## Open Questions

1. **Pricing model:** Free tier limits? Per-computer pricing? Per-session pricing?
2. **Data retention:** How long to keep terminal history? User-configurable?
3. **Bandwidth limits:** Cap on terminal data per session?
4. **Multi-user access:** Allow sharing session view with team members?
5. **Offline web access:** Cache recent session data for offline viewing?

---

## Appendix

### A. Terminal Data Format

Terminal data is streamed as raw character sequences including ANSI escape codes. The web client uses the same rendering logic as the desktop terminal control:

- Character data with position (row, column)
- ANSI escape sequences for colors and formatting
- Cursor position updates
- Screen clear/scroll commands

### B. WebSocket Message Protocol

```json
// Terminal data from CC Director to cloud
{
  "type": "terminal_data",
  "session_id": "uuid",
  "sequence": 12345,
  "data": "raw terminal characters..."
}

// Input from web to CC Director
{
  "type": "input",
  "session_id": "uuid",
  "text": "user input text",
  "special_key": null  // or "enter", "ctrl_c", etc.
}

// Session status update
{
  "type": "status",
  "session_id": "uuid",
  "status": "waiting_input"
}
```

### C. API Endpoints (Vercel)

| Endpoint | Method | Description |
|----------|--------|-------------|
| /api/auth/callback | GET | GitHub OAuth callback |
| /api/computers | GET | List user's computers |
| /api/computers | POST | Register new computer |
| /api/computers/:id | DELETE | Unregister computer |
| /api/sessions | GET | List sessions for computer |
| /api/sessions/:id | GET | Get session details |
| /api/ws | WebSocket | Real-time connection |

---

*Document maintained by: CC Director Team*
*Last updated: February 12, 2026*
