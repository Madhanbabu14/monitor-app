-- Seed Data for Pipeline Control Center
-- Development/Demo data

-- Default Admin User (password: Admin@123)
INSERT INTO users (id, email, display_name, password_hash, role, is_active)
VALUES
  (uuid_generate_v4(), 'admin@company.com', 'Admin User', '$2a$10$B6c.W3zIC9kC9O1yE7/W4.ef9SWH.lIQoBMXgy0T4q8NA9GQ5lkPi', 'Admin', true),
  (uuid_generate_v4(), 'operator@company.com', 'Ops User', '$2a$10$B6c.W3zIC9kC9O1yE7/W4.ef9SWH.lIQoBMXgy0T4q8NA9GQ5lkPi', 'Operator', true),
  (uuid_generate_v4(), 'viewer@company.com', 'View User', '$2a$10$B6c.W3zIC9kC9O1yE7/W4.ef9SWH.lIQoBMXgy0T4q8NA9GQ5lkPi', 'Viewer', true)
ON CONFLICT (email) DO NOTHING;

-- Pipelines
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

-- Pipeline Mappings
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

-- Sample Pipeline Runs
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
        FROM pipelines p WHERE p.name = pname;
    END LOOP;
END;
$$;
