-- Runs once, on an empty data directory, against the database POSTGRES_DB names.
--
-- The two services are split by database rather than by schema. MorphDB operates physical schemas
-- on its own judgement — creating, dropping and rebuilding them is what it is for — and Formbase
-- keeps the raw stream that those projections are rebuilt from. A schema boundary asks MorphDB to
-- stay out of a neighbour it can see; a database boundary means it cannot reach it at all. The
-- isolation costs nothing here: both still run in one server, one container, one volume.

CREATE ROLE formbase WITH LOGIN PASSWORD 'formbase';
CREATE DATABASE formbase OWNER formbase;
