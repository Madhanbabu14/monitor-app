-- ============================================================
-- Monitor.Database :: Script0001_UsedTables
--
-- Tables that the LIVE application (/api/auth, /api/s3, /api/monitor)
-- actually reads/writes today, created first so a fresh environment
-- is usable by the running app as early in the deploy as possible:
--   * users          - /api/auth (local login + Azure AD upsert-by-oid/email)
--   * pipelines      - /api/s3 retrigger flow (pipeline lookup/upsert)
--   * recovery_jobs  - /api/s3 retrigger flow (job record insert)
--
-- Everything else in the original schema.sql is dead weight for the
-- code paths that actually run (see Script0002_RemainingTables.sql)
-- but the Postgres database itself is unchanged, so those tables are
-- still created afterwards for schema parity / any out-of-band
-- consumers of them.
-- ============================================================

CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS "pg_trgm";

-- ============================================================
-- Users & Authentication
-- ============================================================

CREATE TABLE users (
    id              UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    email           VARCHAR(255) UNIQUE NOT NULL,
    display_name    VARCHAR(255) NOT NULL,
    password_hash   VARCHAR(255),
    role            VARCHAR(50) NOT NULL DEFAULT 'Viewer' CHECK (role IN ('Admin', 'Operator', 'Viewer')),
    azure_oid       VARCHAR(255) UNIQUE,
    is_active       BOOLEAN NOT NULL DEFAULT true,
    last_login_at   TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ============================================================
-- Pipelines
-- ============================================================

CREATE TABLE pipelines (
    id              UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    name            VARCHAR(255) UNIQUE NOT NULL,
    display_name    VARCHAR(255) NOT NULL,
    target_table    VARCHAR(255),
    s3_prefix       VARCHAR(500),
    is_active       BOOLEAN NOT NULL DEFAULT true,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ============================================================
-- Recovery Jobs
-- ============================================================

CREATE TABLE recovery_jobs (
    id              UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    pipeline_name   VARCHAR(255) NOT NULL,
    start_date      DATE NOT NULL,
    end_date        DATE NOT NULL,
    files_found     INTEGER NOT NULL DEFAULT 0,
    files_processed INTEGER NOT NULL DEFAULT 0,
    files_failed    INTEGER NOT NULL DEFAULT 0,
    status          VARCHAR(50) NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending','Running','Success','Failed','Partial')),
    triggered_by    VARCHAR(255),
    adf_run_id      VARCHAR(255),
    started_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at    TIMESTAMPTZ,
    error_message   TEXT
);

CREATE INDEX idx_recovery_jobs_pipeline_name ON recovery_jobs(pipeline_name);
CREATE INDEX idx_recovery_jobs_started_at ON recovery_jobs(started_at DESC);

-- ============================================================
-- Updated At Trigger (shared by users/pipelines here, and by
-- pipeline_mappings in Script0002_RemainingTables.sql)
-- ============================================================

CREATE OR REPLACE FUNCTION update_updated_at_column()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER update_users_updated_at BEFORE UPDATE ON users FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();
CREATE TRIGGER update_pipelines_updated_at BEFORE UPDATE ON pipelines FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();
