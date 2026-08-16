#!/usr/bin/env bash
set -euo pipefail

escape_literal() { sed "s/'/''/g" "$1"; }

admin_password="$(< /run/hvo-secrets/SQLSERVER_PASSWORD)"
initializer_password="$(escape_literal /run/hvo-secrets/SQL_INITIALIZER_PASSWORD)"
runtime_password="$(escape_literal /run/hvo-secrets/SQL_RUNTIME_PASSWORD)"
export SQLCMDPASSWORD="$admin_password"

if [[ "$SQL_PROVISION_PHASE" == before ]]; then
  /opt/mssql-tools18/bin/sqlcmd -b -C -S sqlserver -U "$SQL_ADMIN_USER" <<SQL
IF DB_ID(N'$SQL_DATABASE') IS NULL CREATE DATABASE [$SQL_DATABASE];
IF SUSER_ID(N'$SQL_INITIALIZER_USER') IS NULL CREATE LOGIN [$SQL_INITIALIZER_USER] WITH PASSWORD=N'$initializer_password';
IF SUSER_ID(N'$SQL_RUNTIME_USER') IS NULL CREATE LOGIN [$SQL_RUNTIME_USER] WITH PASSWORD=N'$runtime_password';
GO
USE [$SQL_DATABASE];
IF USER_ID(N'$SQL_INITIALIZER_USER') IS NULL CREATE USER [$SQL_INITIALIZER_USER] FOR LOGIN [$SQL_INITIALIZER_USER];
IF USER_ID(N'$SQL_RUNTIME_USER') IS NULL CREATE USER [$SQL_RUNTIME_USER] FOR LOGIN [$SQL_RUNTIME_USER];
SQL
  /opt/mssql-tools18/bin/sqlcmd -b -C -S sqlserver -U "$SQL_ADMIN_USER" -d master -v DatabaseName="$SQL_DATABASE" MigrationUser="$SQL_INITIALIZER_USER" -i /run/hvo-sql/migration-role.sql
elif [[ "$SQL_PROVISION_PHASE" == after ]]; then
  /opt/mssql-tools18/bin/sqlcmd -b -C -S sqlserver -U "$SQL_ADMIN_USER" -d master -v DatabaseName="$SQL_DATABASE" RuntimeUser="$SQL_RUNTIME_USER" -i /run/hvo-sql/runtime-role.sql
else
  exit 64
fi
