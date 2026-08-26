-- ============================================================
-- Monitor.Database :: Seed0001_DevPipelinesAndRuns
--
-- Dev/demo-only data ported from the source repo's
-- database/seeds/seed.sql (the pipelines / pipeline_mappings /
-- sample pipeline_runs sections). Unlike the source file, this
-- script is NOT applied to every environment: Program.cs only
-- includes files under the "Seed" name prefix in the DbUp run when
-- DOTNET_ENVIRONMENT=Development, so production/staging deploys
-- never see this data. It contains no credentials, so - unlike the
-- user seed below it can be a plain, idempotent SQL script.
--
-- Runs after Script0001_UsedTables.sql (pipelines table) and
-- Script0002_RemainingTables.sql (pipeline_mappings, pipeline_runs)
-- thanks to DbUp's ordinal filename sort ("Script..." < "Seed...").
-- ============================================================

-- ============================================================
-- Pipelines
-- ============================================================

INSERT INTO pipelines (id, name, display_name, target_table, s3_prefix, is_active) VALUES
  (uuid_generate_v4(), 'content_object',            'Content Object',            'rt.content_object',            'content_object',            true),
  (uuid_generate_v4(), 'organisation',              'Organisation',              'rt.organisation',              'organisation',              true),
  (uuid_generate_v4(), 'enrolment',                 'Enrolment',                 'rt.enrolment',                 'enrolment',                 true),
  (uuid_generate_v4(), 'user',                      'User',                      'rt.user',                      'user',                      true),
  (uuid_generate_v4(), 'user_session',              'User Session',              'rt.user_sessions',             'user_session',              true),
  (uuid_generate_v4(), 'learning_path',             'Learning Path',             'rt.learning_path',             'learning_path',             true),
  (uuid_generate_v4(), 'learning_path_enrolment',   'Learning Path Enrolment',   'rt.learning_path_enrolment',   'learning_path_enrolment',   true),
  (uuid_generate_v4(), 'folder',                    'Folder',                    'rt.folder',                    'folder',                    true),
  (uuid_generate_v4(), 'attribute_value',           'Attribute Value',           'rt.attribute_value',           'attribute_value',           true)
ON CONFLICT (name) DO NOTHING;

-- ============================================================
-- Pipeline Mappings
-- ============================================================

INSERT INTO pipeline_mappings (id, pipeline_name, target_schema, target_table, s3_prefix, is_active) VALUES
  (uuid_generate_v4(), 'content_object',           'rt', 'content_object',          'content_object',           true),
  (uuid_generate_v4(), 'organisation',             'rt', 'organisation',            'organisation',             true),
  (uuid_generate_v4(), 'enrolment',                'rt', 'enrolment',               'enrolment',                true),
  (uuid_generate_v4(), 'user',                     'rt', 'user',                    'user',                     true),
  (uuid_generate_v4(), 'user_session',             'rt', 'user_sessions',           'user_session',             true),
  (uuid_generate_v4(), 'learning_path',            'rt', 'learning_path',           'learning_path',            true),
  (uuid_generate_v4(), 'learning_path_enrolment',  'rt', 'learning_path_enrolment', 'learning_path_enrolment',  true),
  (uuid_generate_v4(), 'folder',                   'rt', 'folder',                  'folder',                   true),
  (uuid_generate_v4(), 'attribute_value',          'rt', 'attribute_value',         'attribute_value',          true)
ON CONFLICT (pipeline_name) DO NOTHING;

-- ============================================================
-- Sample Pipeline Runs (dashboard demo history)
-- ============================================================

DO $$
DECLARE
    pipeline_names TEXT[] := ARRAY['enrolment','user','content_object','organisation','learning_path'];
    statuses TEXT[] := ARRAY['Success','Success','Success','Failed','Running'];
    pname TEXT;
    pstatus TEXT;
    run_start TIMESTAMPTZ;
    dur BIGINT;
BEGIN
    FOR i IN 1..array_length(pipeline_names, 1) LOOP
        pname := pipeline_names[i];
        pstatus := statuses[i];
        run_start := NOW() - (i || ' hours')::INTERVAL;
        dur := (120 + i * 45) * 1000;

        INSERT INTO pipeline_runs (
            id, pipeline_id, pipeline_name, run_id, status, trigger_type,
            start_time, end_time, duration_ms, files_processed, rows_inserted, rows_updated
        )
        SELECT
            uuid_generate_v4(),
            p.id,
            pname,
            'adf-run-demo-' || i || '-' || extract(epoch from now())::bigint,
            pstatus,
            'Schedule',
            run_start,
            CASE WHEN pstatus != 'Running' THEN run_start + (dur || ' milliseconds')::INTERVAL ELSE NULL END,
            CASE WHEN pstatus != 'Running' THEN dur ELSE NULL END,
            (50 + i * 10),
            (1000 + i * 500),
            (200 + i * 100)
        FROM pipelines p WHERE p.name = pname
        ON CONFLICT (run_id) DO NOTHING;
    END LOOP;
END;
$$;
