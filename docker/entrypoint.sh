#!/bin/sh
# Verb dispatcher for the NodeSet Editor image.
#
#   serve                  run the web application (default when no verb is given)
#   initialize             create the schema and import the bundled Core NodeSet
#   dbtool <args...>       run the database tool directly (migrate, import-file, status, script)
#   <anything else>        executed as-is, so `docker run ... sh` still works
#
# Every verb reads ConnectionStrings__Postgres from the environment.
set -e

SERVER_DLL=/app/NodeSetEditor.Server.dll
DBTOOL_DLL=/app/dbtool/NodeSetEditor.DbTool.dll
CORE_NODESET=/app/seed/Opc.Ua.NodeSet2.xml

verb="${1:-serve}"
[ $# -gt 0 ] && shift

case "$verb" in
   serve)
      exec dotnet "$SERVER_DLL" "$@"
      ;;
   initialize)
      # Creates missing tables only; it will not alter an existing schema. See DESIGN.md §3.
      echo "[initialize] applying schema"
      dotnet "$DBTOOL_DLL" migrate "$@"
      echo "[initialize] importing Core NodeSet"
      dotnet "$DBTOOL_DLL" import-file "$CORE_NODESET" "$@"
      echo "[initialize] done"
      ;;
   dbtool)
      exec dotnet "$DBTOOL_DLL" "$@"
      ;;
   *)
      exec "$verb" "$@"
      ;;
esac
