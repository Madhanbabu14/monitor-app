-- ============================================================
-- Monitor.Database :: Script0002_RemainingTables
--
-- The rest of the original schema.sql. None of these tables are
-- queried by the code behind the three live routes (/api/auth,
-- /api/s3, /api/monitor) as of this migration - the "Pipeline
-- Details / Error Analysis / Data Recovery / DB Validation /
-- Reports / Audit Logs" surface they'd back is aspirational
-- frontend-only nav with no wired backend controller. The monitor
-- dashboard itself reads a separate, externally-managed replica
-- (rt.pipeline_operational_logs), not these tables.
--
-- They are still created here - after the used tables in
-- Script0001_UsedTables.sql - because the underlying Postgres
-- schema is unchanged by this migration; dropping them would be a
-- schema change, not a translation.
-- ============================================================

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

CREATE TRIGGER update_pipeline_mappings_updated_at BEFORE UPDATE ON pipeline_mappings FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

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
