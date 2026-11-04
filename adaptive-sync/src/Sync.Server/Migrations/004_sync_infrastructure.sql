CREATE SEQUENCE sync_row_version_seq;

CREATE TABLE sync_operation_log (
    op_id          uuid    NOT NULL PRIMARY KEY,
    client_id      text    NOT NULL,
    schema_version integer NOT NULL,
    table_name     text    NOT NULL,
    record_id      uuid    NOT NULL,
    op_type        text    NOT NULL,
    logical_time   bigint  NOT NULL,
    strategy       text    NOT NULL,
    outcome        text    NOT NULL,
    row_version    bigint  NOT NULL,
    message        text,
    fields         jsonb   NOT NULL DEFAULT '[]'::jsonb
);

CREATE INDEX ix_sync_operation_log_record ON sync_operation_log (table_name, record_id);

CREATE INDEX ix_player_profile_row_version ON player_profile (row_version);
CREATE INDEX ix_inventory_item_row_version ON inventory_item (row_version);
CREATE INDEX ix_item_modification_row_version ON item_modification (row_version);
