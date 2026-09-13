-- Assigns the minimum privileges the runtime app role needs to operate
-- the NodeSetEditor app: CONNECT on this database, USAGE on schema public,
-- DML on all tables and sequences, plus default privileges so future objects
-- created by the admin role (e.g. via EF migrate) inherit the same grants.
--
-- The role itself must already exist; it is created manually
-- (Postgres roles are cluster-wide, so creating it once per server is enough).
-- Run as the admin role against the target database.
--
-- The app role name is taken from the psql variable ':app_user'. Pass it
-- explicitly with -v app_user=<role> on the psql command line; if omitted
-- it defaults to 'webapp' for backwards compatibility with callers that
-- haven't been updated to pass the parameter.
--
-- The admin role that owns future objects is ':admin_user' (default: 'pgadmin').
--
--   psql ... -v app_user=webapp -v admin_user=pgadmin -f grant_webapp.sql

\set ON_ERROR_STOP on

-- Default :app_user to 'webapp' when the caller did not supply -v app_user=<role>.
\if :{?app_user}
\else
\set app_user webapp
\endif

-- Default :admin_user to 'pgadmin' when the caller did not supply -v admin_user=<role>.
\if :{?admin_user}
\else
\set admin_user pgadmin
\endif

SELECT current_database() AS dbn \gset

GRANT CONNECT ON DATABASE :"dbn" TO :"app_user";
GRANT USAGE   ON SCHEMA public TO :"app_user";
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES    IN SCHEMA public TO :"app_user";
GRANT USAGE, SELECT                  ON ALL SEQUENCES IN SCHEMA public TO :"app_user";

ALTER DEFAULT PRIVILEGES FOR ROLE :"admin_user" IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :"app_user";
ALTER DEFAULT PRIVILEGES FOR ROLE :"admin_user" IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO :"app_user";
