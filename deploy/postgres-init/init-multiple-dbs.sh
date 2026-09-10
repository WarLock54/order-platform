#!/bin/bash
# Database-per-Service: her mikroservis kendi izole veritabanına sahip
# (Proje 1'deki aynı prensip). Postgres container ilk ayağa kalktığında
# orders / inventory / payments veritabanlarını otomatik oluşturur.
set -e

for DB in orders inventory payments; do
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" <<-EOSQL
    SELECT 'CREATE DATABASE $DB'
    WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = '$DB')\gexec
    GRANT ALL PRIVILEGES ON DATABASE $DB TO $POSTGRES_USER;
EOSQL
done
