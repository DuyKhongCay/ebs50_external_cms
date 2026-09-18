-- Only for the observed EBS50 schema and EMPTY links / links_staging tables.
-- Stop the application and back up the database/config first.
-- Execute with sqlite3 -bail. Any error must close the connection without COMMIT.
BEGIN IMMEDIATE;
CREATE TEMP TABLE layering_guard (
    CheckName TEXT NOT NULL,
    Expected INTEGER NOT NULL,
    Actual INTEGER NOT NULL,
    CHECK (Expected = Actual)
);
INSERT INTO layering_guard VALUES ('links empty', 0, (SELECT count(*) FROM links));
INSERT INTO layering_guard VALUES ('links_staging empty', 0, (SELECT count(*) FROM links_staging));
INSERT INTO layering_guard VALUES ('links column count', 4, (SELECT count(*) FROM pragma_table_info('links')));
INSERT INTO layering_guard VALUES ('staging column count', 5, (SELECT count(*) FROM pragma_table_info('links_staging')));
INSERT INTO layering_guard VALUES ('links known columns', 4, (
    SELECT count(*) FROM pragma_table_info('links') WHERE
    (name = 'ID' AND upper(type) = 'VARCHAR(40)' AND pk = 0 AND "notnull" = 1) OR
    (name = 'Variant' AND upper(type) = 'VARCHAR(16)' AND pk = 0 AND "notnull" = 1) OR
    (name = 'MAC' AND upper(type) = 'VARCHAR(16)' AND pk = 1 AND "notnull" = 1) OR
    (name = 'Layer' AND upper(type) = 'VARCHAR(42)' AND pk = 2 AND dflt_value = '''''')
));
INSERT INTO layering_guard VALUES ('staging known columns', 5, (
    SELECT count(*) FROM pragma_table_info('links_staging') WHERE
    (name = 'ID' AND upper(type) = 'VARCHAR(40)' AND pk = 0 AND "notnull" = 0) OR
    (name = 'Variant' AND upper(type) = 'VARCHAR(16)' AND pk = 0 AND "notnull" = 0) OR
    (name = 'MAC' AND upper(type) = 'VARCHAR(16)' AND pk = 1 AND "notnull" = 1) OR
    (name = 'Layer' AND upper(type) = 'VARCHAR(42)' AND pk = 2 AND dflt_value = '''''') OR
    (name = 'DELETE' AND upper(type) = 'VARCHAR(1)' AND pk = 0 AND "notnull" = 0)
));
INSERT INTO layering_guard VALUES ('no custom indexes or triggers', 0, (
    SELECT count(*) FROM sqlite_master
    WHERE tbl_name IN ('links', 'links_staging') AND type IN ('index', 'trigger') AND sql IS NOT NULL
));
-- Refuse dependent views and foreign keys: this procedure is not a general migration.
INSERT INTO layering_guard VALUES ('no dependent views', 0, (
    SELECT count(*) FROM sqlite_master WHERE type = 'view' AND lower(sql) LIKE '%links%'
));
INSERT INTO layering_guard VALUES ('no foreign keys involving links', 0, (
    SELECT count(*) FROM sqlite_master AS m, pragma_foreign_key_list(m.name) AS fk
    WHERE m.type = 'table' AND (m.name IN ('links', 'links_staging') OR fk."table" IN ('links', 'links_staging'))
));
DROP TABLE links;
DROP TABLE links_staging;
CREATE TABLE links (
    ID VARCHAR(40) NOT NULL,
    Variant VARCHAR(16) NOT NULL,
    MAC VARCHAR(16) NOT NULL PRIMARY KEY
);
CREATE TABLE links_staging (
    ID VARCHAR(40),
    Variant VARCHAR(16),
    MAC VARCHAR(16) NOT NULL PRIMARY KEY,
    "DELETE" VARCHAR(1)
);
DROP TABLE layering_guard;
COMMIT;
