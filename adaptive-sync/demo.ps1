function pg($q) { docker exec adaptive-sync-postgres psql -U sync -d adaptive_sync -c $q }
function profiles { pg "select nickname, coins, avatar_id, premium_currency, region, is_deleted from player_profile order by nickname;" }
function decisions { pg "select strategy, schema_version as v, op_type, outcome, message from sync_operation_log where schema_version = 1 order by row_version;" }
function clean { pg "truncate item_modification, inventory_item, player_profile, sync_operation_log;" }
function health { curl.exe -s http://localhost:5080/health; "" }
Clear-Host
