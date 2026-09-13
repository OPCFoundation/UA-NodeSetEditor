-- Removes all permissions held by the given app role on this database.
--
-- This includes:
--   * CONNECT on the database
--   * USAGE on schema public
--   * Any SELECT/INSERT/UPDATE/DELETE granted on existing tables
--   * Any USAGE/SELECT granted on existing sequences
--   * Any ad-hoc grants made outside of grant_webapp.sql
--   * The ALTER DEFAULT PRIVILEGES rules that auto-grant to this role
--
-- The role itself is NOT dropped. Use DROP ROLE separately (or drop_app_user.ps1)
-- if you want to delete the role across the cluster (Postgres roles are
-- cluster-wide; this script only affects the current database).
--
-- ============================================================================
-- WARNING: this script is intended for tearing a role DOWN, not cleaning up
-- before a re-grant. Running it strips every grant the role has on the DB,
-- INCLUDING grants on tables EF migrations have already created. After a
-- revoke, the role can no longer query the existing schema until you re-run
-- grant_webapp.sql against the same DB to restore both the default-privilege
-- rule AND the per-table grants.
--
-- Recovery: psql ... -v app_user=<role> -f grant_webapp.sql
-- ============================================================================
--
-- The role is taken from the psql variable :app_user (default: 'webapp').
-- Run as the admin role against the target database:
--
-- The admin role is ':admin_user' (default: 'pgadmin') and must match the one
-- grant_webapp.sql used, or the default-privilege rule will not be found.
--
--   psql ... -v app_user=webapp -v admin_user=pgadmin -f revoke_webapp.sql

\set ON_ERROR_STOP on

\if :{?app_user}
\else
\set app_user webapp
\endif

\if :{?admin_user}
\else
\set admin_user pgadmin
\endif

SELECT current_database() AS dbn \gset

-- 1. Strip the ALTER DEFAULT PRIVILEGES rules that say "future objects created
--    by :admin_user auto-grant to :app_user". Without this, DROP OWNED would clear
--    today's grants but the next CREATE TABLE would re-grant access to the role.
ALTER DEFAULT PRIVILEGES FOR ROLE :"admin_user" IN SCHEMA public
    REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM :"app_user";
ALTER DEFAULT PRIVILEGES FOR ROLE :"admin_user" IN SCHEMA public
    REVOKE USAGE, SELECT ON SEQUENCES FROM :"app_user";

-- 2. Revoke every privilege the role currently holds on objects in this
--    database (tables, sequences, schema, database) and drop any objects the
--    role owns. This is the Postgres-canonical "scrub everything this role
--    has access to" command -- broader than the explicit REVOKE list because
--    it also catches hand-issued grants.
DROP OWNED BY :"app_user";
