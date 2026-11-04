ALTER TABLE player_profile
    ADD COLUMN region text;

CREATE TABLE item_modification (
    id             uuid    NOT NULL PRIMARY KEY,
    item_id        uuid    NOT NULL REFERENCES inventory_item (id),
    mod_type       text,
    mod_level      bigint  NOT NULL DEFAULT 1,
    row_version    bigint  NOT NULL DEFAULT 0,
    field_versions jsonb   NOT NULL DEFAULT '{}'::jsonb,
    is_deleted     boolean NOT NULL DEFAULT false,
    deleted_at     bigint
);

CREATE INDEX ix_item_modification_item_id ON item_modification (item_id);
