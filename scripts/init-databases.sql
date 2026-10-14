-- Runs once, on an empty data directory, against the database POSTGRES_DB names.
--
-- The two services are split by database rather than by schema. MorphDB operates physical schemas
-- on its own judgement — creating, dropping and rebuilding them is what it is for — and Formbase
-- keeps the raw stream that those projections are rebuilt from. A schema boundary asks MorphDB to
-- stay out of a neighbour it can see; a database boundary means it cannot reach it at all. The
-- isolation costs nothing here: both still run in one server, one container, one volume.

--
-- It runs as the image's bootstrap superuser, which is for administration only: neither service logs
-- in as it. MorphDB refuses a superuser or BYPASSRLS login (from 0.19.0) -- PostgreSQL applies no
-- row-level security to one -- so it gets a role of its own that owns its database.

CREATE ROLE morph WITH LOGIN PASSWORD 'morph' NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
ALTER DATABASE morphdb OWNER TO morph;
-- The role MorphDB switches a request to when it bypasses row-level security (the master secret).
-- It logs in to nothing and needs no special attribute; MorphDB 0.19+ refuses to start without
-- the membership, and MorphDB 0.18 ignores it.
CREATE ROLE morphdb_bypass NOLOGIN;
GRANT morphdb_bypass TO morph;

CREATE ROLE formbase WITH LOGIN PASSWORD 'formbase';
CREATE DATABASE formbase OWNER formbase;
