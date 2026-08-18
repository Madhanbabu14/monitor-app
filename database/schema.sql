-- Pipeline Control Center - PostgreSQL Schema
-- Version: 1.0.0

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
-- Pipeline Runs
-- ============================================================

CREATE TABLE pipeline_runs (
    id              UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    pipeline_id     UUID REFERENCES pipelines(id) ON DELETE SET NULL,
    pipeline_name   VARCHAR(255) NOT NULL,
    run_id          VARCHAR(255) UNIQUE NOT NULL,
    status          VARCHAR(50) NOT NULL CHECK (status IN ('Success','Failed','Running','Cancelled','Warning','Queued')),
    trigger_type    VARCHAR(100) NOT NULL DEFAULT 'Manual',
    start_time      TIMESTAMPTZ,
    end_time        TIMESTAMPTZ,
    duration_ms     BIGINT,
    files_processed INTEGER NOT NULL DEFAULT 0,
    rows_inserted   BIGINT NOT NULL DEFAULT 0,
    rows_updated    BIGINT NOT NULL DEFAULT 0,
    error_message   TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_pipeline_runs_pipeline_name ON pipeline_runs(pipeline_name);
CREATE INDEX idx_pipeline_runs_status ON pipeline_runs(status);
CREATE INDEX idx_pipeline_runs_start_time ON pipeline_runs(start_time DESC);
CREATE INDEX idx_pipeline_runs_created_at ON pipeline_runs(created_at DESC);

-- ============================================================
-- Activities
-- ============================================================

CREATE TABLE activities (
    id               UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    pipeline_run_id  UUID NOT NULL REFERENCES pipeline_runs(id) ON DELETE CASCADE,
    activity_name    VARCHAR(255) NOT NULL,
    activity_type    VARCHAR(100),
    status           VARCHAR(50) NOT NULL,
    start_time       TIMESTAMPTZ,
    end_time         TIMESTAMPTZ,
    duration_ms      BIGINT,
    error_message    TEXT,
    input            JSONB,
    output           JSONB,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_activities_pipeline_run_id ON activities(pipeline_run_id);
CREATE INDEX idx_activities_status ON activities(status);

-- ============================================================
-- Pipeline Errors
-- ============================================================

CREATE TABLE pipeline_errors (
    id               UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    pipeline_run_id  UUID REFERENCES pipeline_runs(id) ON DELETE SET NULL,
    pipeline_name    VARCHAR(255) NOT NULL,
    failed_activity  VARCHAR(255),
    error_message    TEXT NOT NULL,
    error_code       VARCHAR(100),
    file_name        VARCHAR(500),
    run_id           VARCHAR(255) NOT NULL,
    timestamp        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_pipeline_errors_pipeline_name ON pipeline_errors(pipeline_name);
CREATE INDEX idx_pipeline_errors_timestamp ON pipeline_errors(timestamp DESC);
CREATE INDEX idx_pipeline_errors_run_id ON pipeline_errors(run_id);

-- ============================================================
-- Pipeline Mappings (Pipeline → Database Table)
-- ============================================================

CREATE TABLE pipeline_mappings (
    id             UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    pipeline_name  VARCHAR(255) UNIQUE NOT NULL,
    target_schema  VARCHAR(100) NOT NULL DEFAULT 'rt',
    target_table   VARCHAR(255) NOT NULL,
    s3_prefix      VARCHAR(500),
    is_active      BOOLEAN NOT NULL DEFAULT true,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ============================================================
-- Validation Results
-- ============================================================

CREATE TABLE validation_results (
    id                    UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    pipeline_id           UUID REFERENCES pipelines(id) ON DELETE SET NULL,
    pipeline_name         VARCHAR(255) NOT NULL,
    target_table          VARCHAR(255) NOT NULL,
    total_records         BIGINT NOT NULL DEFAULT 0,
    latest_modified_date  TIMESTAMPTZ,
    records_updated_today INTEGER NOT NULL DEFAULT 0,
    validation_status     VARCHAR(50) NOT NULL CHECK (validation_status IN ('Healthy','Warning','Error')),
    validation_message    TEXT,
    validated_by          VARCHAR(255),
    validated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_validation_results_pipeline_name ON validation_results(pipeline_name);
CREATE INDEX idx_validation_results_validated_at ON validation_results(validated_at DESC);

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
-- Pipeline Run Files (for missing file tracking)
-- ============================================================

CREATE TABLE pipeline_run_files (
    id               UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    pipeline_run_id  UUID REFERENCES pipeline_runs(id) ON DELETE SET NULL,
    pipeline_name    VARCHAR(255) NOT NULL,
    file_name        VARCHAR(500) NOT NULL,
    s3_key           VARCHAR(1000),
    status           VARCHAR(50) NOT NULL DEFAULT 'Processed' CHECK (status IN ('Processed','Failed','Skipped')),
    rows_inserted    INTEGER DEFAULT 0,
    rows_updated     INTEGER DEFAULT 0,
    processed_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_pipeline_run_files_pipeline_name ON pipeline_run_files(pipeline_name);
CREATE INDEX idx_pipeline_run_files_processed_at ON pipeline_run_files(processed_at);
CREATE INDEX idx_pipeline_run_files_file_name ON pipeline_run_files(file_name);

-- ============================================================
-- Audit Logs
-- ============================================================

CREATE TABLE audit_logs (
    id          UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    user_id     VARCHAR(255),
    user_email  VARCHAR(255) NOT NULL,
    action      VARCHAR(100) NOT NULL,
    resource    VARCHAR(255),
    details     JSONB,
    ip_address  INET,
    user_agent  TEXT,
    timestamp   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_audit_logs_user_email ON audit_logs(user_email);
CREATE INDEX idx_audit_logs_action ON audit_logs(action);
CREATE INDEX idx_audit_logs_timestamp ON audit_logs(timestamp DESC);

-- ============================================================
-- Updated At Trigger
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
CREATE TRIGGER update_pipeline_mappings_updated_at BEFORE UPDATE ON pipeline_mappings FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();
