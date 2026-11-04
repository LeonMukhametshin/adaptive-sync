ALTER TABLE player_profile
    ADD COLUMN avatar_id        text,
    ADD COLUMN premium_currency bigint NOT NULL DEFAULT 0;

ALTER TABLE inventory_item
    ADD COLUMN durability bigint NOT NULL DEFAULT 100;
