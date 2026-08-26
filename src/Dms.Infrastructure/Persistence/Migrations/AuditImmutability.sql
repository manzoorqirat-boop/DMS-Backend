-- Third and final layer of audit-trail protection: enforced by the database itself, so it
-- holds even for a direct psql session with the application's own credentials.
--
-- The entity exposes no mutators and DmsDbContext rejects modified/deleted audit entries, but
-- both of those live inside the application. 21 CFR Part 11 §11.10(e) requires that the trail
-- not obscure previously recorded information, and a trail that anyone holding the connection
-- string can rewrite does not meet that bar in any way an inspector would accept.
--
-- Apply this in the EF migration that creates dms.audit_events, via migrationBuilder.Sql(...).
-- It is deliberately NOT a runtime concern: the guarantee has to exist in the schema.
--
-- Written to be safely re-runnable (CREATE OR REPLACE / DROP ... IF EXISTS), because
-- StartupMigrator's EnsureCreated fallback applies it on a path that may execute more than
-- once. A plain CREATE TRIGGER would fail on the second run and take startup down with it.

CREATE OR REPLACE FUNCTION dms.reject_audit_mutation()
RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION 'dms.audit_events is append-only; % is not permitted', TG_OP
        USING ERRCODE = 'restrict_violation';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS audit_events_no_update ON dms.audit_events;
CREATE TRIGGER audit_events_no_update
    BEFORE UPDATE ON dms.audit_events
    FOR EACH ROW EXECUTE FUNCTION dms.reject_audit_mutation();

DROP TRIGGER IF EXISTS audit_events_no_delete ON dms.audit_events;
CREATE TRIGGER audit_events_no_delete
    BEFORE DELETE ON dms.audit_events
    FOR EACH ROW EXECUTE FUNCTION dms.reject_audit_mutation();

-- Note on TRUNCATE: it fires neither of the row-level triggers above. Guard it by not granting
-- TRUNCATE to the application role — the app never needs it:
--
--   REVOKE TRUNCATE ON dms.audit_events FROM dms_app;
--
-- A table owner can still drop the triggers, so the application role must not own the schema
-- in a deployed environment. Migrations run as a separate, higher-privileged role.
