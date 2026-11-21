#!/bin/sh
set -eu
IFS= read -r MSSQL_SA_PASSWORD </run/lab-sql/bootstrap
test -n "$MSSQL_SA_PASSWORD"
export MSSQL_SA_PASSWORD
exec /opt/mssql/bin/sqlservr
