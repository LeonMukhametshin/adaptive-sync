CREATE TABLE schema_registry (
    table_name    text    NOT NULL,
    field_name    text    NOT NULL,
    introduced_in integer NOT NULL,
    category      text    NOT NULL,
    data_type     text    NOT NULL,
    PRIMARY KEY (table_name, field_name)
);

CREATE TABLE player_profile (
    id             uuid    NOT NULL PRIMARY KEY,
    nickname       text,
    level          bigint  NOT NULL DEFAULT 1,
    coins          bigint  NOT NULL DEFAULT 0,
    settings_sound boolean NOT NULL DEFAULT true,
    row_version    bigint  NOT NULL DEFAULT 0,
    field_versions jsonb   NOT NULL DEFAULT '{}'::jsonb,
    is_deleted     boolean NOT NULL DEFAULT false,
    deleted_at     bigint
);

CREATE TABLE inventory_item (
    id             uuid    NOT NULL PRIMARY KEY,
    player_id      uuid    NOT NULL REFERENCES player_profile (id),
    item_type      text,
    quantity       bigint  NOT NULL DEFAULT 0,
    row_version    bigint  NOT NULL DEFAULT 0,
    field_versions jsonb   NOT NULL DEFAULT '{}'::jsonb,
    is_deleted     boolean NOT NULL DEFAULT false,
    deleted_at     bigint
);

CREATE INDEX ix_inventory_item_player_id ON inventory_item (player_id);
