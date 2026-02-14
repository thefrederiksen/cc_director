-- CC Director Remote Control - Initial Database Schema
-- Run this in Supabase SQL Editor: https://supabase.com/dashboard/project/zpmvwkuesrashertpmwi/sql

-- ============================================
-- COMPUTERS TABLE
-- Registered CC Director installations
-- ============================================
CREATE TABLE computers (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID NOT NULL REFERENCES auth.users(id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    machine_id TEXT NOT NULL,
    registered_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_seen TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    is_online BOOLEAN NOT NULL DEFAULT FALSE,

    -- Each user can only register a machine once
    UNIQUE(user_id, machine_id)
);

-- Index for looking up computers by user
CREATE INDEX idx_computers_user_id ON computers(user_id);

-- Index for looking up by machine_id (for reconnection)
CREATE INDEX idx_computers_machine_id ON computers(machine_id);

-- ============================================
-- SESSIONS TABLE
-- Claude Code sessions running on computers
-- ============================================
CREATE TABLE sessions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    computer_id UUID NOT NULL REFERENCES computers(id) ON DELETE CASCADE,
    repo_path TEXT NOT NULL,
    repo_name TEXT NOT NULL,
    started_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ended_at TIMESTAMPTZ,
    status TEXT NOT NULL DEFAULT 'running' CHECK (status IN ('running', 'waiting_input', 'completed', 'error')),

    -- Session metadata
    claude_session_id TEXT,
    custom_name TEXT
);

-- Index for looking up sessions by computer
CREATE INDEX idx_sessions_computer_id ON sessions(computer_id);

-- Index for active sessions
CREATE INDEX idx_sessions_status ON sessions(status) WHERE status IN ('running', 'waiting_input');

-- ============================================
-- TERMINAL_DATA TABLE
-- Streamed terminal output chunks
-- ============================================
CREATE TABLE terminal_data (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id UUID NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    sequence BIGINT NOT NULL,
    data TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- Each session has unique sequence numbers
    UNIQUE(session_id, sequence)
);

-- Index for retrieving terminal data in order
CREATE INDEX idx_terminal_data_session_sequence ON terminal_data(session_id, sequence);

-- Partition hint: If terminal_data grows large, consider partitioning by session_id

-- ============================================
-- ROW LEVEL SECURITY (RLS)
-- Users can only see their own data
-- ============================================

-- Enable RLS on all tables
ALTER TABLE computers ENABLE ROW LEVEL SECURITY;
ALTER TABLE sessions ENABLE ROW LEVEL SECURITY;
ALTER TABLE terminal_data ENABLE ROW LEVEL SECURITY;

-- Computers: Users can only see/modify their own computers
CREATE POLICY "Users can view own computers"
    ON computers FOR SELECT
    USING (auth.uid() = user_id);

CREATE POLICY "Users can insert own computers"
    ON computers FOR INSERT
    WITH CHECK (auth.uid() = user_id);

CREATE POLICY "Users can update own computers"
    ON computers FOR UPDATE
    USING (auth.uid() = user_id);

CREATE POLICY "Users can delete own computers"
    ON computers FOR DELETE
    USING (auth.uid() = user_id);

-- Sessions: Users can only see sessions on their computers
CREATE POLICY "Users can view own sessions"
    ON sessions FOR SELECT
    USING (
        computer_id IN (
            SELECT id FROM computers WHERE user_id = auth.uid()
        )
    );

CREATE POLICY "Users can insert own sessions"
    ON sessions FOR INSERT
    WITH CHECK (
        computer_id IN (
            SELECT id FROM computers WHERE user_id = auth.uid()
        )
    );

CREATE POLICY "Users can update own sessions"
    ON sessions FOR UPDATE
    USING (
        computer_id IN (
            SELECT id FROM computers WHERE user_id = auth.uid()
        )
    );

CREATE POLICY "Users can delete own sessions"
    ON sessions FOR DELETE
    USING (
        computer_id IN (
            SELECT id FROM computers WHERE user_id = auth.uid()
        )
    );

-- Terminal data: Users can only see terminal data for their sessions
CREATE POLICY "Users can view own terminal data"
    ON terminal_data FOR SELECT
    USING (
        session_id IN (
            SELECT s.id FROM sessions s
            JOIN computers c ON s.computer_id = c.id
            WHERE c.user_id = auth.uid()
        )
    );

CREATE POLICY "Users can insert own terminal data"
    ON terminal_data FOR INSERT
    WITH CHECK (
        session_id IN (
            SELECT s.id FROM sessions s
            JOIN computers c ON s.computer_id = c.id
            WHERE c.user_id = auth.uid()
        )
    );

-- Terminal data is append-only, no updates or deletes from client
-- (cleanup would be done via service role or database function)

-- ============================================
-- REALTIME SUBSCRIPTIONS
-- Enable realtime for live terminal updates
-- ============================================

-- Enable realtime on terminal_data for live streaming
ALTER PUBLICATION supabase_realtime ADD TABLE terminal_data;

-- Enable realtime on sessions for status updates
ALTER PUBLICATION supabase_realtime ADD TABLE sessions;

-- Enable realtime on computers for online status
ALTER PUBLICATION supabase_realtime ADD TABLE computers;

-- ============================================
-- HELPER FUNCTIONS
-- ============================================

-- Function to update computer's last_seen timestamp
CREATE OR REPLACE FUNCTION update_computer_last_seen(computer_uuid UUID)
RETURNS VOID AS $$
BEGIN
    UPDATE computers
    SET last_seen = NOW()
    WHERE id = computer_uuid AND user_id = auth.uid();
END;
$$ LANGUAGE plpgsql SECURITY DEFINER;

-- Function to mark computer online/offline
CREATE OR REPLACE FUNCTION set_computer_online(computer_uuid UUID, online BOOLEAN)
RETURNS VOID AS $$
BEGIN
    UPDATE computers
    SET is_online = online, last_seen = NOW()
    WHERE id = computer_uuid AND user_id = auth.uid();
END;
$$ LANGUAGE plpgsql SECURITY DEFINER;
