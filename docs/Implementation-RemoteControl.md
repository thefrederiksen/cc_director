# Remote Control - Implementation Plan

## Overview

This document outlines the phased implementation of the CC Director Remote Control feature. Each step is marked with who does the work:

- **[USER]** - Manual configuration steps you need to do
- **[CODE]** - I will write the code
- **[BOTH]** - Collaborative step

**Repository:** Everything is open source in the cc_director repo.

---

## Phase 1: Infrastructure Setup

Set up the external services (Supabase, Vercel) that the feature depends on.

### Step 1.1: Create Supabase Project [USER]

1. Go to https://supabase.com and sign up/login
2. Create a new project (name: `cc-director` or similar)
3. Wait for project to provision (~2 minutes)
4. Save these values (Settings > API):
   - Project URL (e.g., `https://xxxxx.supabase.co`)
   - Anon/Public key
   - Service role key (keep secret)

### Step 1.2: Configure GitHub OAuth in Supabase [USER]

1. In Supabase: Authentication > Providers > GitHub
2. Enable GitHub provider
3. Go to GitHub: Settings > Developer Settings > OAuth Apps > New OAuth App
   - Application name: `CC Director`
   - Homepage URL: `https://your-vercel-domain.vercel.app` (placeholder for now)
   - Callback URL: `https://xxxxx.supabase.co/auth/v1/callback`
4. Copy Client ID and Client Secret from GitHub to Supabase
5. Save configuration

### Step 1.3: Create Database Schema [CODE]

I will create SQL migration scripts for:
- `computers` table
- `sessions` table
- `terminal_data` table
- Row Level Security (RLS) policies
- Indexes for performance

**Output:** `cloud/supabase/migrations/001_initial_schema.sql`

### Step 1.4: Run Database Migration [USER]

1. In Supabase: SQL Editor
2. Paste and run the migration script I provide
3. Verify tables created in Table Editor

### Step 1.5: Create Vercel Project [USER]

1. Go to https://vercel.com and sign up/login (use GitHub login)
2. Import the cc_director repository
3. Set root directory to `cloud/web` (we'll create this)
4. Add environment variables:
   - `NEXT_PUBLIC_SUPABASE_URL` = your Supabase project URL
   - `NEXT_PUBLIC_SUPABASE_ANON_KEY` = your Supabase anon key
   - `SUPABASE_SERVICE_ROLE_KEY` = your Supabase service role key
5. Deploy (will fail initially until we add code - that's OK)

### Step 1.6: Update GitHub OAuth Callback [USER]

1. Go back to GitHub OAuth App settings
2. Update Homepage URL to your actual Vercel domain
3. Callback URL stays as Supabase URL

---

## Phase 2: Cloud Service (Next.js on Vercel)

Build the web application that serves the remote control interface.

### Step 2.1: Project Scaffolding [CODE]

Create Next.js project structure:

```
cloud/
  web/
    src/
      app/              # Next.js App Router pages
      components/       # React components
      lib/              # Utilities, Supabase client
    public/
    package.json
    next.config.js
    tsconfig.json
```

### Step 2.2: Authentication Pages [CODE]

- Login page with GitHub OAuth button
- Auth callback handler
- Protected route middleware
- User session management

### Step 2.3: API Routes [CODE]

- `POST /api/computers` - Register a computer
- `GET /api/computers` - List user's computers
- `DELETE /api/computers/[id]` - Unregister computer
- `GET /api/sessions` - List sessions for a computer
- `GET /api/sessions/[id]` - Get session with terminal data

### Step 2.4: WebSocket Handler [CODE]

Using Vercel's Edge Runtime or Supabase Realtime:
- Handle connections from CC Director clients
- Relay terminal data to web clients
- Relay input from web to CC Director

### Step 2.5: Initial Deployment [BOTH]

1. [CODE] I commit the cloud/web folder
2. [USER] Push to GitHub, Vercel auto-deploys
3. [BOTH] Verify deployment works, fix any issues

---

## Phase 3: CC Director Desktop Changes

Add remote control capabilities to the Windows application.

### Step 3.1: Configuration Storage [CODE]

- Store Supabase URL and keys in app settings
- Store user authentication token
- Store computer registration ID

**Files:**
- `src/CcDirector.Core/Cloud/CloudSettings.cs`

### Step 3.2: Authentication Service [CODE]

- Open browser for GitHub OAuth flow
- Handle OAuth callback (local HTTP listener)
- Store and refresh access tokens

**Files:**
- `src/CcDirector.Core/Cloud/AuthService.cs`
- `src/CcDirector.Wpf/Dialogs/LoginDialog.xaml`

### Step 3.3: Computer Registration [CODE]

- Generate unique machine ID
- Register computer with cloud service
- Store registration locally

**Files:**
- `src/CcDirector.Core/Cloud/RegistrationService.cs`
- `src/CcDirector.Wpf/Dialogs/RegisterComputerDialog.xaml`

### Step 3.4: WebSocket Connection Manager [CODE]

- Persistent connection to cloud service
- Automatic reconnection with exponential backoff
- Connection status events

**Files:**
- `src/CcDirector.Core/Cloud/CloudConnectionManager.cs`

### Step 3.5: Terminal Stream Publisher [CODE]

- Hook into existing terminal buffer
- Stream character data to cloud
- Batch updates for efficiency

**Files:**
- `src/CcDirector.Core/Cloud/TerminalStreamPublisher.cs`

### Step 3.6: Remote Input Handler [CODE]

- Receive input commands from cloud
- Inject into terminal session
- Handle special keys

**Files:**
- `src/CcDirector.Core/Cloud/RemoteInputHandler.cs`

### Step 3.7: UI Integration [CODE]

- Menu items (File > Remote Control > ...)
- Status bar indicator
- Settings page for cloud configuration

**Files:**
- `src/CcDirector.Wpf/MainWindow.xaml` (menu additions)
- `src/CcDirector.Wpf/Views/SettingsPage.xaml` (new section)

### Step 3.8: Desktop Testing [BOTH]

1. [CODE] Write unit tests for cloud services
2. [USER] Test registration flow end-to-end
3. [BOTH] Debug any issues

---

## Phase 4: Web Client UI

Build the responsive web interface for viewing and controlling sessions.

### Step 4.1: Dashboard Page [CODE]

- List of registered computers with online status
- Quick session overview
- Navigation to session view

### Step 4.2: Terminal Component [CODE]

- Render terminal characters with ANSI colors
- Match CC Director desktop appearance
- Scrollback buffer support

### Step 4.3: Session View Page [CODE]

- Full terminal display
- Input text box
- Session info sidebar
- Special key buttons (Ctrl+C, etc.)

### Step 4.4: Mobile Responsive Design [CODE]

- Responsive layout for phone/tablet
- Touch-friendly controls
- Collapsible sidebar

### Step 4.5: Real-time Updates [CODE]

- Subscribe to terminal data changes
- Live connection status
- Session status updates

---

## Phase 5: Integration & Polish

Connect everything and ensure it works reliably.

### Step 5.1: End-to-End Testing [BOTH]

1. Register CC Director with cloud
2. Start a Claude session
3. View session on phone browser
4. Send input from phone
5. Verify input executes on desktop

### Step 5.2: Error Handling [CODE]

- Graceful offline handling
- Clear error messages
- Retry mechanisms

### Step 5.3: Performance Optimization [CODE]

- Terminal data batching
- Efficient database queries
- WebSocket message compression

### Step 5.4: Documentation [CODE]

- User guide for setup
- Troubleshooting guide
- Update features.html

---

## Project Structure (Final)

```
cc_director/
  cloud/
    web/                    # Next.js web app (Vercel)
      src/
        app/
          page.tsx          # Landing/login
          dashboard/
          session/[id]/
        components/
          Terminal.tsx
          SessionList.tsx
        lib/
          supabase.ts
    supabase/
      migrations/
        001_initial_schema.sql
  src/
    CcDirector.Core/
      Cloud/
        AuthService.cs
        CloudConnectionManager.cs
        CloudSettings.cs
        RegistrationService.cs
        RemoteInputHandler.cs
        TerminalStreamPublisher.cs
    CcDirector.Wpf/
      Dialogs/
        LoginDialog.xaml
        RegisterComputerDialog.xaml
  docs/
    PRD-RemoteControl.md
    Implementation-RemoteControl.md
```

---

## Estimated Effort by Phase

| Phase | Primary Work | Dependencies |
|-------|--------------|--------------|
| Phase 1 | User setup | None |
| Phase 2 | Web/API code | Phase 1 complete |
| Phase 3 | Desktop code | Phase 1 complete |
| Phase 4 | Web UI code | Phase 2 complete |
| Phase 5 | Integration | Phases 2-4 complete |

**Note:** Phases 2 and 3 can run in parallel after Phase 1.

---

## Getting Started

To begin implementation, we need to complete **Phase 1** first. This requires you to:

1. Create a Supabase account and project
2. Create a Vercel account
3. Set up GitHub OAuth app

Once you have Supabase project credentials, I can generate the database schema and we can proceed to coding.

---

*Document maintained by: CC Director Team*
*Last updated: February 12, 2026*
