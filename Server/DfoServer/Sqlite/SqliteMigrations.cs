using System;
using System.Collections.Generic;
using DfoServer.Game.Currency;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;
using DfoServer.Game.TitleBook;
using Microsoft.Data.Sqlite;

namespace DfoServer.Sqlite
{
    // 版本化迁移: PRAGMA user_version 门控, 每条迁移每库只跑一次。
    //
    // 规则:
    //   1) 新增迁移 = 在 Steps 末尾追加下一个版本号, 禁止修改/删除已发布条目。
    //   2) item_schema.sql 始终保持"新库的完整最终形态"; 迁移只负责把旧库升上来。
    //      加列时两边都要写(schema + 迁移), 否则旧库缺列。
    //   3) 迁移体保持幂等(加列先查存在/重建先查建表SQL)作双保险, 但版本门控保证正常路径只执行一次。
    //   4) 破坏性变更(删列/改约束)用表重建或 DROP COLUMN, SQL 批内嵌 BEGIN/COMMIT 保证原子性。
    internal static class SqliteMigrations
    {
        private static readonly (int Version, string Name, Action<SqliteConnection> Apply)[] Steps =
        {
            (1, "accounts 账号级货币列", conn => SqliteSchemaMigrator.EnsureColumns(conn, "accounts", new[]
            {
                ("cera", "INTEGER NOT NULL DEFAULT 0"),
                ("token_cera", "INTEGER NOT NULL DEFAULT 0"),
                ("happy_token_cera", "INTEGER NOT NULL DEFAULT 0"),
                ("lucky_star", "INTEGER NOT NULL DEFAULT 0"),
            })),

            // 原 SqliteCharacterRepository 构造函数内散装补列(含原 InventoryMigrationRunner 独有4列)
            (2, "characters town/外观/进度列", conn => SqliteSchemaMigrator.EnsureColumns(conn, "characters", new[]
            {
                ("direction", "INTEGER NOT NULL DEFAULT 5"),
                ("area_state", "INTEGER NOT NULL DEFAULT 3"),
                ("name_bytes", "BLOB"),
                ("appearance_blob", "BLOB"),
                ("delete_flag", "INTEGER NOT NULL DEFAULT 0"),
                ("exp", "INTEGER NOT NULL DEFAULT 0"),
                ("ex_equip_slot_stat", "INTEGER NOT NULL DEFAULT 0"),
                ("pvp_grade", "INTEGER NOT NULL DEFAULT 0"),
                ("pvp_rating_grade", "INTEGER NOT NULL DEFAULT 0"),
                ("user_state", "INTEGER NOT NULL DEFAULT 0"),
                ("bonus_sp", "INTEGER NOT NULL DEFAULT 0"),
                ("bonus_tp", "INTEGER NOT NULL DEFAULT 0"),
                ("clone_title_item_id", "INTEGER NOT NULL DEFAULT 0"),
            })),

            (3, "character_equipped_entries 期限/锁列", conn => SqliteSchemaMigrator.EnsureColumns(conn, "character_equipped_entries", new[]
            {
                ("expire_time", "INTEGER NOT NULL DEFAULT 0"),
                ("equipment_lock_id", "INTEGER NOT NULL DEFAULT 0"),
            })),

            (4, "character_items equipment_lock_id 列", conn => SqliteSchemaMigrator.EnsureColumns(conn, "character_items", new[]
            {
                ("equipment_lock_id", "INTEGER NOT NULL DEFAULT 0"),
            })),

            (5, "character_item_locks 表重建", SqliteSchemaMigrator.MigrateCharacterItemLocks),

            (6, "character_items 唯一键重建(含item_kind)", SqliteSchemaMigrator.MigrateCharacterItemsUniqueConstraint),

            (7, "character_init_flags 角色选项blob列", conn => SqliteSchemaMigrator.EnsureColumns(conn, "character_init_flags", new[]
            {
                ("character_option_blob", "BLOB"),
            })),

            (8, "accounts 晶块6列", conn => SqliteSchemaMigrator.EnsureColumns(conn, "accounts", new[]
            {
                ("cube_black", "INTEGER NOT NULL DEFAULT 0"),
                ("cube_white", "INTEGER NOT NULL DEFAULT 0"),
                ("cube_red", "INTEGER NOT NULL DEFAULT 0"),
                ("cube_blue", "INTEGER NOT NULL DEFAULT 0"),
                ("cube_clear", "INTEGER NOT NULL DEFAULT 0"),
                ("cube_gold", "INTEGER NOT NULL DEFAULT 0"),
            })),

            (9, "晶块从 character_items 归集账号", CurrencyService.MigrateCubeFragmentsFromCharacterItems),

            // 原 AccountCharacterEntryRepository.SaveAll 内散装补列
            (10, "account_character_entries 选角条目列", conn => SqliteSchemaMigrator.EnsureColumns(conn, "account_character_entries", new[]
            {
                ("entry_index", "INTEGER NOT NULL DEFAULT 0"),
                ("slot_index", "INTEGER NOT NULL DEFAULT 0"),
                ("name", "TEXT NOT NULL DEFAULT ''"),
                ("name_bytes", "BLOB"),
                ("body_after_name", "BLOB NOT NULL DEFAULT X''"),
            })),

            // 原 SqliteUserInfoBlobRepository.SaveGetUserInfoResponseBlob 内散装补列
            (11, "get_userinfo_template response_blob 列", conn => SqliteSchemaMigrator.EnsureColumns(conn, "get_userinfo_template", new[]
            {
                ("response_blob", "BLOB"),
            })),

            // characters.gold/coin 是创建时写入后再无人读写的影子列(游戏内金币=character_items slot0,
            // 点券=accounts.cera), 留着只会误导调试。schema 已同步移除。
            (12, "characters 删除影子列 gold/coin", conn =>
                SqliteSchemaMigrator.DropColumnsIfExist(conn, "characters", "gold", "coin")),

            // character_init_flags 18列死数据清理(d85b887, 种子DB验证):
            // A) 被 account_settings/account_premiums 覆盖: hotkey_key_type, main_game_option_blob,
            //    quickchat_bank0/1, ack_premium_blob
            // B) 种子值全零且无动态写入: 其余13列(mailbox_*×4, ack_*等)
            (13, "character_init_flags 删除18列死数据", conn =>
                SqliteSchemaMigrator.DropColumnsIfExist(conn, "character_init_flags",
                    "hotkey_key_type", "main_game_option_blob", "quickchat_bank0", "quickchat_bank1",
                    "ack_premium_blob",
                    "shop_coin_event_flag", "level60_ui_state", "boss_tower_placeholder",
                    "event_info_tail_byte", "mailbox_loaded_count", "mailbox_mode",
                    "mailbox_not_loaded_count", "mailbox_unknown_count_c", "ack_account_reg_time",
                    "ack_quest_display_ids", "racing_dungeon_group_flags", "ack_post_tutorial_u16",
                    "ack_unread_tail")),

            // 高频点查缺失索引 + audit_log 溯源索引(只写表, 人工查账用)
            (14, "补齐点查索引 + audit_log 索引", conn => ExecuteBatch(conn, @"
CREATE INDEX IF NOT EXISTS idx_character_items_char_template
    ON character_items(character_id, list_type, item_template_id);
CREATE INDEX IF NOT EXISTS idx_character_creatures_key
    ON character_creatures(character_id, creature_key);
CREATE INDEX IF NOT EXISTS idx_dungeon_permissions_dungeon
    ON character_dungeon_permissions(character_id, dungeon_id);
CREATE INDEX IF NOT EXISTS idx_item_audit_log_char_time
    ON item_audit_log(character_id, created_at);")),

            // 每日/周常重置状态表(复活币每日领取等), 见 DailyResetService
            (15, "character_daily_reset/counters 每日重置", conn => ExecuteBatch(conn, @"
CREATE TABLE IF NOT EXISTS character_daily_reset (
    character_id INTEGER PRIMARY KEY,
    day_id       INTEGER NOT NULL DEFAULT 0,
    week_id      INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS character_daily_counters (
    character_id INTEGER NOT NULL,
    counter_key  TEXT    NOT NULL,
    period       TEXT    NOT NULL DEFAULT 'day' CHECK (period IN ('day', 'week')),
    value        INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (character_id, counter_key),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);")),

            (16, "accounts 赛丽亚幸运值列", conn => SqliteSchemaMigrator.EnsureColumns(conn, "accounts", new[]
            {
                ("seria_luck_value", "INTEGER NOT NULL DEFAULT 0"),
            })),

            // 黑暗武士组合技能页使用专用表；旧 character_init_bodies(0x01FD) 内容不迁移、不再读写。
            (17, "character_dark_knight_combo_skill_pages 专用表", conn => ExecuteBatch(conn, @"
CREATE TABLE IF NOT EXISTS character_dark_knight_combo_skill_pages (
    character_id INTEGER NOT NULL,
    page_index INTEGER NOT NULL CHECK (page_index >= 0 AND page_index <= 1),
    body BLOB NOT NULL,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (character_id, page_index),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);")),

            (18, "character_subtype0_fields mood_value", conn => SqliteSchemaMigrator.EnsureColumns(conn, "character_subtype0_fields", new[]
            {
                ("mood_value", "INTEGER NOT NULL DEFAULT 0"),
            })),

            (19, "accounts honor_exp", conn => SqliteSchemaMigrator.EnsureColumns(conn, "accounts", new[]
            {
                ("honor_exp", "INTEGER NOT NULL DEFAULT 0"),
            })),

            (20, "accounts growth_capsule_exp", conn => SqliteSchemaMigrator.EnsureColumns(conn, "accounts", new[]
            {
                ("growth_capsule_exp", "INTEGER NOT NULL DEFAULT 0"),
            })),

            (21, "character_creatures extra_json", conn => SqliteSchemaMigrator.EnsureColumns(conn, "character_creatures", new[]
            {
                ("extra_json", "TEXT NOT NULL DEFAULT '{}'"),
            })),

            // 封包回放机制退役: 选角 init 流不再从库里回放原始字节。
            // 仍在使用的数据迁入三张专用表, 其余 noti 改为动态构建:
            //   768(0x0300 晶体契约) → character_crystal_contract, 存量选择原样保留
            //   855(0x0357 租赁物品) → character_rental_items, 存量按旧存储编码解析后原样保留
            //   119(0x0077 宠物欢迎语) → character_pet_welcome_cache; 旧布局 occurrence 0=包体,
            //       occurrence 1000=缓存对应的宠物模板ID; 无模板ID元数据的行(旧抓包残留/
            //       新角色占位)在旧代码下也永远命中不了缓存, 不迁移
            //   53(0x0035 赛丽亚币): 从 accounts 钱包动态构建
            //   415(0x019F 支援兵): 发包流始终用动态包替换, 存量行从不被读取
            //   273(0x0111 联合服好友)/984(0x03D8 增率抽奖)/351(0x015F 制作技能点):
            //       统一发空态(新角色既有基线), 回放数据对单机服务端无意义
            //   897(0x0381 收集箱): builder 早已从 PVF + collectbox 进度表动态构建
            // 最后删除三张回放表(character_init_bodies/packet_sequence/getuserinfo_extra_packets)
            // 与 get_userinfo_template.response_blob 列(登录响应整包模板, 已无调用方)
            (22, "封包回放表退役 + 数据库整备(成就单一存储/表正名/死表死列清理)", conn =>
            {
                // 新库的 schema 已不再创建回放表; 补一个空壳让搬运语句在新库上也能跑, 结尾统一删除
                ExecuteBatch(conn, @"
CREATE TABLE IF NOT EXISTS character_init_bodies (
    character_id INTEGER NOT NULL,
    noti_type INTEGER NOT NULL,
    occurrence_index INTEGER NOT NULL DEFAULT 0,
    body BLOB NOT NULL,
    PRIMARY KEY (character_id, noti_type, occurrence_index)
);
CREATE TABLE IF NOT EXISTS character_crystal_contract (
    character_id INTEGER PRIMARY KEY,
    cube_type INTEGER NOT NULL DEFAULT 0,
    cube_grade INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS character_rental_items (
    character_id INTEGER NOT NULL,
    shop_entry_id INTEGER NOT NULL,
    inventory_template_id INTEGER NOT NULL DEFAULT 0,
    expire_time INTEGER NOT NULL,
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_character_rental_items_char
    ON character_rental_items(character_id);
CREATE TABLE IF NOT EXISTS character_pet_welcome_cache (
    character_id INTEGER PRIMARY KEY,
    item_template_id INTEGER NOT NULL DEFAULT 0,
    body BLOB,
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);");

                // 晶体契约: 2 字节 body → cube_type/cube_grade 两列
                var contracts = new System.Collections.Generic.List<(int cid, byte type, byte grade)>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT character_id, body FROM character_init_bodies WHERE noti_type = 768;";
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var body = reader.IsDBNull(1) ? null : (byte[])reader[1];
                            if (body != null && body.Length >= 2)
                                contracts.Add((reader.GetInt32(0), body[0], body[1]));
                        }
                    }
                }
                foreach (var row in contracts)
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "INSERT OR REPLACE INTO character_crystal_contract(character_id, cube_type, cube_grade) VALUES(@cid, @t, @g);";
                        cmd.Parameters.AddWithValue("@cid", row.cid);
                        cmd.Parameters.AddWithValue("@t", (int)row.type);
                        cmd.Parameters.AddWithValue("@g", (int)row.grade);
                        cmd.ExecuteNonQuery();
                    }
                }

                // 租赁物品: 旧存储编码 → 每件一行
                // 半迁移重跑时清掉上次未完成的搬运行, 保证幂等
                ExecuteBatch(conn, "DELETE FROM character_rental_items;");
                var rentals = new System.Collections.Generic.List<(int cid, byte[] body)>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT character_id, body FROM character_init_bodies WHERE noti_type = 855;";
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            rentals.Add((reader.GetInt32(0), reader.IsDBNull(1) ? null : (byte[])reader[1]));
                    }
                }
                foreach (var row in rentals)
                {
                    var rental = new Game.SelectCharacter.RentalInfoSnapshot();
                    Game.SelectCharacter.RentalInfoSnapshot.ParseStorageBody(row.body, rental);
                    foreach (var item in rental.Items)
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = @"INSERT INTO character_rental_items(character_id, shop_entry_id, inventory_template_id, expire_time)
VALUES(@cid, @sid, @inv, @exp);";
                            cmd.Parameters.AddWithValue("@cid", row.cid);
                            cmd.Parameters.AddWithValue("@sid", (long)item.ItemId);
                            cmd.Parameters.AddWithValue("@inv", (long)item.InventoryTemplateId);
                            cmd.Parameters.AddWithValue("@exp", (long)item.ExpireTime);
                            cmd.ExecuteNonQuery();
                        }
                    }
                }

                // 宠物欢迎语缓存: 只搬有模板ID元数据的有效缓存
                var petCaches = new System.Collections.Generic.List<(int cid, int itemId, byte[] body)>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT meta.character_id, meta.body, msg.body
FROM character_init_bodies meta
LEFT JOIN character_init_bodies msg
    ON msg.character_id = meta.character_id AND msg.noti_type = 119 AND msg.occurrence_index = 0
WHERE meta.noti_type = 119 AND meta.occurrence_index = 1000;";
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var meta = reader.IsDBNull(1) ? null : (byte[])reader[1];
                            if (meta == null || meta.Length < 4)
                                continue;
                            petCaches.Add((
                                reader.GetInt32(0),
                                BitConverter.ToInt32(meta, 0),
                                reader.IsDBNull(2) ? null : (byte[])reader[2]));
                        }
                    }
                }
                foreach (var row in petCaches)
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"INSERT OR REPLACE INTO character_pet_welcome_cache(character_id, item_template_id, body)
VALUES(@cid, @item, @body);";
                        cmd.Parameters.AddWithValue("@cid", row.cid);
                        cmd.Parameters.AddWithValue("@item", row.itemId);
                        cmd.Parameters.AddWithValue("@body", (object)row.body ?? DBNull.Value);
                        cmd.ExecuteNonQuery();
                    }
                }

                // 收尾: 三张回放表与 response_blob 列一并删除
                SqliteSchemaMigrator.DropColumnsIfExist(conn, "get_userinfo_template", "response_blob");
                ExecuteBatch(conn, @"
DROP TABLE IF EXISTS character_init_bodies;
DROP TABLE IF EXISTS packet_sequence;
DROP TABLE IF EXISTS getuserinfo_extra_packets;");

                // ── 以下为数据库整备(与回放退役同一迁移) ──
            // 1) 成就双存储收敛: character_achievement(blob, 运行时进度) 与
            //    character_achievement_complete(结构化, 选角快照) 存同类数据、两套写路径,
            //    迟早互相覆盖。复合主键改为 (character_id, achievement_id) 后, blob 里的
            //    进度条目(较新)并入结构化表, blob 表删除, 全系统单一存储。
            // 2) 表正名(早期误判, IDA 已定论): racing_dungeon_* → daily_challenge_*(NOTI 0x0286),
            //    unknown725 → daily_schedule_states(0x02D5), unknown730 → buy_restrict_items(0x02DA)
            // 3) champion_break_blob(9字节: i32+u8+i32) 拆为三个整数列
            // 4) 删除无写入方且全库为空的 character_pvp_results / character_abuse_values /
            //    global_server_event_phase, 以及花名册抓包回放缓存 account_character_entries
            //    (唯一调用方已死, 现行 GET_USERINFO 从 characters 表动态构建, 存量名单早已过时)
            // 5) account_settings 外键补 ON DELETE CASCADE(全库唯一漏配的一张)
                // ── 成就双存储收敛 ──
                ExecuteBatch(conn, @"
CREATE TABLE IF NOT EXISTS character_achievement_complete (
    character_id INTEGER NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    achievement_id INTEGER NOT NULL,
    p1 INTEGER NOT NULL DEFAULT 0,
    p2 INTEGER NOT NULL DEFAULT 0,
    p3 INTEGER NOT NULL DEFAULT 0,
    p4 INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (character_id, achievement_id),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);");
                if (!IsPrimaryKeyColumn(conn, "character_achievement_complete", "achievement_id"))
                {
                    ExecuteBatch(conn, @"
DROP TABLE IF EXISTS character_achievement_complete_new;
CREATE TABLE character_achievement_complete_new (
    character_id INTEGER NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    achievement_id INTEGER NOT NULL,
    p1 INTEGER NOT NULL DEFAULT 0,
    p2 INTEGER NOT NULL DEFAULT 0,
    p3 INTEGER NOT NULL DEFAULT 0,
    p4 INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (character_id, achievement_id),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);
INSERT OR REPLACE INTO character_achievement_complete_new
    SELECT character_id, sort_order, achievement_id, p1, p2, p3, p4
    FROM character_achievement_complete ORDER BY sort_order;
DROP TABLE character_achievement_complete;
ALTER TABLE character_achievement_complete_new RENAME TO character_achievement_complete;");
                }
                if (TableExists(conn, "character_achievement"))
                {
                    var blobs = new System.Collections.Generic.List<(int cid, byte[] blob)>();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT character_id, achievement FROM character_achievement;";
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                                blobs.Add((reader.GetInt32(0), reader.IsDBNull(1) ? null : (byte[])reader[1]));
                        }
                    }
                    foreach (var row in blobs)
                    {
                        if (row.blob == null)
                            continue;
                        for (var off = 0; off + 12 <= row.blob.Length; off += 12)
                        {
                            var questId = BitConverter.ToInt32(row.blob, off);
                            if (questId <= 0)
                                continue;
                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.CommandText = @"
INSERT INTO character_achievement_complete(character_id, sort_order, achievement_id, p1, p2, p3, p4)
VALUES(@cid,
       COALESCE((SELECT sort_order FROM character_achievement_complete WHERE character_id=@cid AND achievement_id=@aid),
                (SELECT COALESCE(MAX(sort_order),-1)+1 FROM character_achievement_complete WHERE character_id=@cid)),
       @aid, @p1, @p2, @p3, @p4)
ON CONFLICT(character_id, achievement_id)
DO UPDATE SET p1=excluded.p1, p2=excluded.p2, p3=excluded.p3, p4=excluded.p4;";
                                cmd.Parameters.AddWithValue("@cid", row.cid);
                                cmd.Parameters.AddWithValue("@aid", questId);
                                cmd.Parameters.AddWithValue("@p1", (int)BitConverter.ToUInt16(row.blob, off + 4));
                                cmd.Parameters.AddWithValue("@p2", (int)BitConverter.ToUInt16(row.blob, off + 6));
                                cmd.Parameters.AddWithValue("@p3", (int)BitConverter.ToUInt16(row.blob, off + 8));
                                cmd.Parameters.AddWithValue("@p4", (int)BitConverter.ToUInt16(row.blob, off + 10));
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }
                    ExecuteBatch(conn, "DROP TABLE character_achievement;");
                }

                // ── 表正名 ──
                RenameTableIfExists(conn, "character_racing_dungeon_groups", "character_daily_challenge_groups");
                RenameTableIfExists(conn, "character_racing_dungeon_entries", "character_daily_challenge_entries");
                RenameTableIfExists(conn, "character_racing_dungeon_tail_ids", "character_daily_challenge_tail_ids");
                RenameTableIfExists(conn, "character_unknown725", "character_daily_schedule_states");
                RenameTableIfExists(conn, "character_unknown730", "character_buy_restrict_items");

                // ── champion_break_blob 拆列 ──
                SqliteSchemaMigrator.EnsureColumns(conn, "character_init_flags", new[]
                {
                    ("champion_break_key_id", "INTEGER NOT NULL DEFAULT 0"),
                    ("champion_break_mode", "INTEGER NOT NULL DEFAULT 0"),
                    ("champion_break_value", "INTEGER NOT NULL DEFAULT 0"),
                });
                if (ColumnExists(conn, "character_init_flags", "champion_break_blob"))
                {
                    var rows = new System.Collections.Generic.List<(int cid, byte[] blob)>();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT character_id, champion_break_blob FROM character_init_flags WHERE champion_break_blob IS NOT NULL;";
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                                rows.Add((reader.GetInt32(0), (byte[])reader[1]));
                        }
                    }
                    foreach (var row in rows)
                    {
                        if (row.blob == null || row.blob.Length < 9)
                            continue;
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = @"
UPDATE character_init_flags
SET champion_break_key_id=@k, champion_break_mode=@m, champion_break_value=@v
WHERE character_id=@cid;";
                            cmd.Parameters.AddWithValue("@cid", row.cid);
                            cmd.Parameters.AddWithValue("@k", BitConverter.ToInt32(row.blob, 0));
                            cmd.Parameters.AddWithValue("@m", (int)row.blob[4]);
                            cmd.Parameters.AddWithValue("@v", BitConverter.ToInt32(row.blob, 5));
                            cmd.ExecuteNonQuery();
                        }
                    }
                    SqliteSchemaMigrator.DropColumnsIfExist(conn, "character_init_flags", "champion_break_blob");
                }
                // ack_reserved_8b: SELECT ACK 客户端读取边界之后的尾字节, handler 不读;
                // 存量值是抓包切片错位的残渣, 直接删列(builder 固定写零)
                SqliteSchemaMigrator.DropColumnsIfExist(conn, "character_init_flags", "ack_reserved_8b");

                // ── 死表/死列清理 ──
                // account_character_entries: 选角花名册的抓包字节回放缓存, 唯一调用方早已
                // 改为从 characters 表动态构建(存量种子名单也早已与真实角色对不上)
                // character_event_info: 抓包时代的活动列表, event_data 全库全零,
                // 除种子角色外所有角色一直空态; 0x006C 统一发空列表
                ExecuteBatch(conn, @"
DROP TABLE IF EXISTS character_pvp_results;
DROP TABLE IF EXISTS character_abuse_values;
DROP TABLE IF EXISTS global_server_event_phase;
DROP TABLE IF EXISTS account_character_entries;
DROP TABLE IF EXISTS character_event_info;");

                // ── account_settings 外键补 CASCADE ──
                if (TableExists(conn, "account_settings")
                    && !TableSqlContains(conn, "account_settings", "ON DELETE CASCADE"))
                {
                    ExecuteBatch(conn, @"
DROP TABLE IF EXISTS account_settings_new;
CREATE TABLE account_settings_new (
    account_id INTEGER PRIMARY KEY,
    main_game_option BLOB,
    quickchat_bank0 BLOB,
    quickchat_bank1 BLOB,
    hotkey_key_type INTEGER NOT NULL DEFAULT 0,
    hotkey_slots BLOB,
    FOREIGN KEY (account_id) REFERENCES accounts(account_id) ON DELETE CASCADE
);
INSERT INTO account_settings_new SELECT account_id, main_game_option, quickchat_bank0, quickchat_bank1, hotkey_key_type, hotkey_slots FROM account_settings;
DROP TABLE account_settings;
ALTER TABLE account_settings_new RENAME TO account_settings;");
                }
            }),

            // 23: SP/TP 派生化——点数由 SkillPointLedger 从已学技能全量重算, 不再持久化余额。
            // 删 character_skill_points(派生值)和 character_skill_tail(协议镜像);
            // character_skills 直接清空重建为新结构(去掉 page_header 镜像列), 不搬运历史行:
            // 全部角色下次选角时由统一建构器按 (job, growType, 觉醒, 等级) 重建技能面板,
            // 学习进度清零、SP/TP 随派生回满, 杜绝任何旧口径残留。
            (23, "技能点数派生化, 退役镜像表, 技能面板清零重建", MigrateSkillPointDerivation),

            // 24: characters 角色槽位互换支持——新增 slot_index 列并用 character_id 顺序填充初始值
            (24, "characters slot_index 列", conn =>
            {
                SqliteSchemaMigrator.EnsureColumns(conn, "characters", new[]
                {
                    ("slot_index", "INTEGER NOT NULL DEFAULT 0"),
                });
                // 对已有数据按 account_id 分组、character_id 升序分配 slot_index(0,1,2...)
                // 仅更新未删除角色，避免给已删除行写入重叠值
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
UPDATE characters SET slot_index = (
    SELECT cnt FROM (
        SELECT c1.character_id,
               (SELECT COUNT(*) FROM characters c2
                WHERE c2.account_id = c1.account_id
                  AND c2.delete_flag = 0
                  AND c2.character_id <= c1.character_id) - 1 AS cnt
        FROM characters c1
        WHERE c1.character_id = characters.character_id
    )
) WHERE delete_flag = 0;";
                    cmd.ExecuteNonQuery();
                }
            }),

            // 存量角色的 0/1 原样保留（视为已购买）；仅改变今后省略该列时的默认状态。
            (25, "skill_tree_index 未购买状态与默认值", MigrateSkillTreeIndexDefault),

            (26, "守护者盾牌5槽", MigrateKnightShieldDeck),

            (27, "角色金币携带/拍卖额双上限", conn => ExecuteBatch(conn, @"
CREATE TABLE IF NOT EXISTS character_gold_limits (
    character_id       INTEGER PRIMARY KEY,
    gold_carry_limit   INTEGER NOT NULL,
    auction_gold_limit INTEGER NOT NULL,
    updated_at         TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);")),

            // 旧 CHANGE_EMOTION 曾把 1..9 的心情值同时镜像到两个外观字段。
            // v28 只执行一次；此后业务对 emotion_index/action_byte 的合法修改不会再被清理。
            (28, "清理心情处理器写入的外观字段污染", conn => ExecuteBatch(conn, @"
UPDATE character_subtype0_fields
SET emotion_index = 0,
    action_byte = 0
WHERE emotion_index BETWEEN 1 AND 9
  AND action_byte = emotion_index;")),

            (29, "character_new_items/character_avatar_detail ItemCore 迁移", InventoryNewItemMigrationService.Migrate),
            (30, "account_cargo_new_items ItemCore 迁移", InventoryNewItemMigrationService.MigrateAccountCargo),
            (31, "character_avatar_detail 时装 UID 独立", MigrateAvatarDetailIndependentUid),
            (32, "ItemCore charm 归并为 equipment", NormalizeLegacyCharmItemKind),
            (33, "character_avatar_uid_sequence", EnsureAvatarUidSequence),
            (34, "character_creature_uid_sequence", EnsureCreatureUidSequence),
            (35, "character_new_titlebook ItemCore 迁移", CharacterTitleBookRepository.MigrateLegacyToNewTable),
            (36, "character_new_items 主背包虚拟槽0-2迁移", InventoryNewItemMigrationService.MigrateMainVirtualCurrencySlots),
            (37, "inventory_audit_log_v2 新背包审计日志", EnsureInventoryAuditLogV2),
            (38, "character_name_tag_state", NameTagStateRepository.EnsureTableAndMigrateLegacy),
            (39, "character_tower_of_despair_progress", conn => ExecuteBatch(conn, @"
CREATE TABLE IF NOT EXISTS character_tower_of_despair_progress (
    character_id INTEGER PRIMARY KEY,
    highest_cleared_floor INTEGER NOT NULL DEFAULT 0
        CHECK (highest_cleared_floor >= 0 AND highest_cleared_floor <= 100),
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);")),

            (40, "mailbox persistence on ItemCore inventory", MigrateMailboxItemCore),
            (41, "mailbox sender snapshot survives character deletion", MigrateMailboxSenderSnapshot),
            (42, "mercenary expedition assignments and reward outbox", MigrateMercenaryExpedition),

            (43, "quest progress CAS and event inbox", MigrateQuestProgressConcurrency),

            (44, "daily challenge reward claims", conn => ExecuteBatch(conn, @"
CREATE TABLE IF NOT EXISTS character_daily_challenge_claims (
    character_id INTEGER NOT NULL,
    group_index INTEGER NOT NULL CHECK (group_index >= 0 AND group_index < 6),
    claimed_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (character_id, group_index),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);")),

            (45, "dungeon persistent effect outbox", conn => ExecuteBatch(conn, @"
CREATE TABLE IF NOT EXISTS dungeon_persistent_effect_outbox (
    source_event_id TEXT NOT NULL,
    effect_kind TEXT NOT NULL,
    effect_scope INTEGER NOT NULL,
    scope_target INTEGER NOT NULL,
    character_id INTEGER NOT NULL DEFAULT 0,
    account_id INTEGER NOT NULL DEFAULT 0,
    payload_version INTEGER NOT NULL,
    payload_json TEXT NOT NULL,
    state INTEGER NOT NULL DEFAULT 0 CHECK (state >= 0 AND state <= 4),
    lease_id TEXT,
    lease_owner TEXT,
    lease_expires_at INTEGER NOT NULL DEFAULT 0,
    attempt_count INTEGER NOT NULL DEFAULT 0,
    last_error TEXT NOT NULL DEFAULT '',
    result_version INTEGER,
    result_json TEXT,
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL,
    committed_at INTEGER,
    PRIMARY KEY (source_event_id, effect_kind, effect_scope, scope_target)
);
CREATE INDEX IF NOT EXISTS idx_dungeon_effect_outbox_character_state
    ON dungeon_persistent_effect_outbox(character_id, state, updated_at);
CREATE INDEX IF NOT EXISTS idx_dungeon_effect_outbox_account_state
    ON dungeon_persistent_effect_outbox(account_id, state, updated_at);")),

            (46, "character quest notify selections", conn => ExecuteBatch(conn, @"
CREATE TABLE IF NOT EXISTS character_quest_notify_selections (
    character_id INTEGER NOT NULL,
    slot_index INTEGER NOT NULL CHECK (slot_index >= 0 AND slot_index < 4),
    quest_id INTEGER NOT NULL CHECK (quest_id > 0),
    PRIMARY KEY (character_id, slot_index),
    UNIQUE (character_id, quest_id),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);")),

            (47, "account dungeon difficulty permissions", conn => ExecuteBatch(conn, @"
CREATE TABLE IF NOT EXISTS account_dungeon_permissions (
    account_id INTEGER NOT NULL,
    dungeon_id INTEGER NOT NULL CHECK (dungeon_id > 0 AND dungeon_id <= 65535),
    clear_state INTEGER NOT NULL CHECK (clear_state > 0 AND clear_state <= 255),
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (account_id, dungeon_id),
    FOREIGN KEY (account_id) REFERENCES accounts(account_id) ON DELETE CASCADE
);")),

            (48, "character expert job domain state", MigrateExpertJobState),
            (49, "附魔师设备耐久", MigrateEnchanterEndurance),
            (50, "quest activation identity and per-activation event inbox",
                MigrateQuestActivationIdentity),
            (51, "account increase chance lottery progress", conn => ExecuteBatch(conn, @"
CREATE TABLE IF NOT EXISTS account_increase_chance_lottery_progress (
    account_id INTEGER NOT NULL,
    item_template_id INTEGER NOT NULL,
    reward_index INTEGER NOT NULL CHECK(reward_index >= 0 AND reward_index < 20),
    PRIMARY KEY (account_id, item_template_id, reward_index),
    FOREIGN KEY (account_id) REFERENCES accounts(account_id) ON DELETE CASCADE
);")),
            (52, "independent fair-PvP skill trees", conn => ExecuteBatch(conn, @"
CREATE TABLE IF NOT EXISTS character_pvp_skill_state (
    character_id INTEGER PRIMARY KEY,
    initialized_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS character_pvp_skills (
    character_id INTEGER NOT NULL,
    page_index INTEGER NOT NULL CHECK (page_index >= 0 AND page_index <= 1),
    slot INTEGER NOT NULL,
    skill_id INTEGER NOT NULL,
    level INTEGER NOT NULL,
    extra_values BLOB,
    PRIMARY KEY (character_id, page_index, slot),
    UNIQUE (character_id, page_index, skill_id),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);")),

            (53, "daily challenge entry reward claims", conn => ExecuteBatch(conn, @"
CREATE TABLE IF NOT EXISTS character_daily_challenge_entry_claims (
    character_id INTEGER NOT NULL,
    group_index INTEGER NOT NULL CHECK (group_index >= 0 AND group_index < 6),
    entry_index INTEGER NOT NULL CHECK (entry_index >= 0),
    quest_id INTEGER NOT NULL CHECK (quest_id > 0),
    claimed_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (character_id, group_index, entry_index),
    FOREIGN KEY (character_id, group_index, entry_index)
        REFERENCES character_daily_challenge_entries(character_id, group_index, entry_index)
        ON DELETE CASCADE
);")),

            (54, "project claimed challenge entries into client clear flags", conn => ExecuteBatch(conn, @"
INSERT INTO character_invisible_falgs (character_id, slot_index, flag_value)
SELECT character_id, quest_id, 1
FROM character_daily_challenge_entry_claims
WHERE 1 = 1
ON CONFLICT(character_id, slot_index)
DO UPDATE SET flag_value = excluded.flag_value;")),

            (55, "daily challenge authoritative dungeon-clear events", conn => ExecuteBatch(conn, @"
CREATE TABLE IF NOT EXISTS character_daily_challenge_progress_events (
    character_id INTEGER NOT NULL,
    source_event_id TEXT NOT NULL,
    group_index INTEGER NOT NULL CHECK (group_index >= 0 AND group_index < 6),
    entry_index INTEGER NOT NULL CHECK (entry_index >= 0),
    quest_id INTEGER NOT NULL CHECK (quest_id > 0),
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (character_id, source_event_id, group_index, entry_index),
    FOREIGN KEY (character_id, group_index, entry_index)
        REFERENCES character_daily_challenge_entries(character_id, group_index, entry_index)
        ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_daily_challenge_progress_events_character
    ON character_daily_challenge_progress_events(character_id, created_at);")),

            (56, "daily challenge special progress and settlement dedupe", conn => ExecuteBatch(conn, @"
CREATE TABLE IF NOT EXISTS character_daily_challenge_special_state (
    character_id INTEGER PRIMARY KEY,
    challenge_type INTEGER NOT NULL CHECK (challenge_type > 0),
    target_value INTEGER NOT NULL CHECK (target_value > 0),
    progress_value INTEGER NOT NULL DEFAULT 0
        CHECK (progress_value >= 0 AND progress_value <= target_value),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS character_daily_challenge_special_progress_events (
    character_id INTEGER NOT NULL,
    source_event_id TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (character_id, source_event_id),
    FOREIGN KEY (character_id) REFERENCES character_daily_challenge_special_state(character_id)
        ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_daily_challenge_special_events_character
    ON character_daily_challenge_special_progress_events(character_id, created_at);")),
        };

        private static void MigrateEnchanterEndurance(SqliteConnection connection)
        {
            SqliteSchemaMigrator.EnsureColumns(
                connection,
                "character_expert_job",
                new[]
                {
                    ("enchanter_endurance", "INTEGER NOT NULL DEFAULT 0 CHECK(enchanter_endurance >= 0)"),
                });
            if (!TableExists(connection, "character_subtype0_fields"))
                return;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
UPDATE character_expert_job
SET enchanter_endurance=@endurance,
    updated_at=CURRENT_TIMESTAMP
WHERE enchanter_endurance=0
  AND character_id IN (
      SELECT character_id
      FROM character_subtype0_fields
      WHERE expert_job_type=1
  );";
                command.Parameters.AddWithValue(
                    "@endurance",
                    EnchanterConfigProvider.Config.InitialEndurance);
                command.ExecuteNonQuery();
            }
        }

        private static void MigrateExpertJobState(SqliteConnection connection)
        {
            ExecuteBatch(connection, @"
CREATE TABLE IF NOT EXISTS character_expert_job (
    character_id INTEGER PRIMARY KEY,
    giveup_count INTEGER NOT NULL DEFAULT 0 CHECK(giveup_count >= 0 AND giveup_count <= 65535),
    disjoint_machine_grade INTEGER NOT NULL DEFAULT 0 CHECK(disjoint_machine_grade >= 0),
    disjoint_machine_endurance INTEGER NOT NULL DEFAULT 0 CHECK(disjoint_machine_endurance >= 0),
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS character_expert_job_recipes (
    character_id INTEGER NOT NULL,
    recipe_id INTEGER NOT NULL CHECK(recipe_id > 0),
    PRIMARY KEY (character_id, recipe_id),
    FOREIGN KEY (character_id) REFERENCES character_expert_job(character_id) ON DELETE CASCADE
);");

            var legacyStates = new List<(int CharacterId, byte Mode, ExpertJobState State)>();
            var hasLegacyBlob = ColumnExists(
                connection,
                "character_init_flags",
                "expert_job_blob");
            var hasSubtype0Fields = TableExists(
                connection,
                "character_subtype0_fields");
            if (hasLegacyBlob)
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT character_id, expert_job_blob
FROM character_init_flags
WHERE expert_job_blob IS NOT NULL;";
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var blob = reader.IsDBNull(1) ? null : (byte[])reader[1];
                            if (!ExpertJobStateCodec.TryDecodeLegacyBlob(
                                    blob,
                                    out var mode,
                                    out var state))
                            {
                                throw new InvalidOperationException(
                                    $"invalid legacy expert job state: " +
                                    $"cid={reader.GetInt32(0)} length={blob?.Length ?? 0}");
                            }

                            if (mode == 0)
                                continue;
                            legacyStates.Add((reader.GetInt32(0), mode, state));
                        }
                    }
                }
            }

            using (var transaction = connection.BeginTransaction())
            {
                if (hasSubtype0Fields)
                {
                    ExecuteBatch(connection, transaction, @"
INSERT OR IGNORE INTO character_expert_job (character_id)
SELECT character_id
FROM character_subtype0_fields
WHERE expert_job_type > 0;");
                }

                foreach (var legacy in legacyStates)
                {
                    var machine = legacy.State.DisjointMachine;
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"
INSERT INTO character_expert_job (
    character_id, giveup_count,
    disjoint_machine_grade, disjoint_machine_endurance, updated_at)
VALUES (@cid, @giveup, @grade, @endurance, CURRENT_TIMESTAMP)
ON CONFLICT(character_id) DO UPDATE SET
    giveup_count=excluded.giveup_count,
    disjoint_machine_grade=excluded.disjoint_machine_grade,
    disjoint_machine_endurance=excluded.disjoint_machine_endurance,
    updated_at=CURRENT_TIMESTAMP;";
                        command.Parameters.AddWithValue("@cid", legacy.CharacterId);
                        command.Parameters.AddWithValue("@giveup", legacy.State.GiveUpCount);
                        command.Parameters.AddWithValue(
                            "@grade",
                            legacy.Mode == ExpertJobStateCodec.DisjointerMode
                                ? machine?.MachineGrade ?? 0
                                : 0);
                        command.Parameters.AddWithValue(
                            "@endurance",
                            legacy.Mode == ExpertJobStateCodec.DisjointerMode
                                ? machine?.Endurance ?? 0
                                : 0);
                        command.ExecuteNonQuery();
                    }

                    if (legacy.Mode == ExpertJobStateCodec.DisjointerMode)
                        continue;

                    foreach (var recipeId in legacy.State.LearnedRecipeIds)
                    {
                        if (recipeId <= 0)
                            continue;
                        using (var command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = @"
INSERT OR IGNORE INTO character_expert_job_recipes (character_id, recipe_id)
VALUES (@cid, @recipe);";
                            command.Parameters.AddWithValue("@cid", legacy.CharacterId);
                            command.Parameters.AddWithValue("@recipe", recipeId);
                            command.ExecuteNonQuery();
                        }
                    }
                }

                transaction.Commit();
            }

            if (hasLegacyBlob)
            {
                SqliteSchemaMigrator.DropColumnsIfExist(
                    connection,
                    "character_init_flags",
                    "expert_job_blob");
            }
        }

        private static void MigrateMercenaryExpedition(SqliteConnection connection)
        {
            ExecuteBatch(connection, @"
CREATE TABLE IF NOT EXISTS account_mercenary_assignments (
    assignment_id INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id INTEGER NOT NULL,
    character_id INTEGER NOT NULL UNIQUE,
    character_level INTEGER NOT NULL,
    start_time INTEGER NOT NULL,
    finish_time INTEGER NOT NULL,
    area_index INTEGER NOT NULL,
    period_index INTEGER NOT NULL,
    avatar_bonus_tier INTEGER NOT NULL DEFAULT 0,
    status INTEGER NOT NULL DEFAULT 1,
    version INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (account_id) REFERENCES accounts(account_id) ON DELETE CASCADE,
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE RESTRICT
);
CREATE INDEX IF NOT EXISTS idx_mercenary_assignments_account
    ON account_mercenary_assignments(account_id, character_id);

CREATE TABLE IF NOT EXISTS mercenary_reward_outbox (
    outbox_id INTEGER PRIMARY KEY AUTOINCREMENT,
    assignment_id INTEGER NOT NULL UNIQUE,
    mailbox_message_id INTEGER,
    account_id INTEGER NOT NULL,
    character_id INTEGER NOT NULL,
    area_index INTEGER NOT NULL,
    period_index INTEGER NOT NULL,
    completed_hours INTEGER NOT NULL DEFAULT 0,
    is_early_return INTEGER NOT NULL DEFAULT 0,
    return_purpose INTEGER NOT NULL DEFAULT 0,
    base_gold INTEGER NOT NULL DEFAULT 0,
    bonus_gold INTEGER NOT NULL DEFAULT 0,
    item_template_id INTEGER NOT NULL DEFAULT 0,
    item_count INTEGER NOT NULL DEFAULT 0,
    mail_title_key TEXT NOT NULL,
    mail_message_key TEXT NOT NULL,
    critical_multiplier_milli INTEGER NOT NULL DEFAULT 1000,
    delivery_status TEXT NOT NULL DEFAULT 'pending',
    delivery_attempts INTEGER NOT NULL DEFAULT 0,
    last_delivery_error TEXT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    delivered_at TEXT,
    FOREIGN KEY (mailbox_message_id) REFERENCES mailbox_messages(message_id) ON DELETE SET NULL,
    FOREIGN KEY (account_id) REFERENCES accounts(account_id) ON DELETE CASCADE,
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE RESTRICT
);
CREATE INDEX IF NOT EXISTS idx_mercenary_outbox_delivery
    ON mercenary_reward_outbox(delivery_status, outbox_id);

CREATE TABLE IF NOT EXISTS mercenary_reward_items (
    outbox_id INTEGER NOT NULL,
    ordinal INTEGER NOT NULL,
    item_template_id INTEGER NOT NULL CHECK(item_template_id > 0),
    item_count INTEGER NOT NULL CHECK(item_count > 0),
    PRIMARY KEY (outbox_id, ordinal),
    FOREIGN KEY (outbox_id) REFERENCES mercenary_reward_outbox(outbox_id) ON DELETE CASCADE
);
");
        }

        private static void MigrateMailboxItemCore(SqliteConnection connection)
        {
            SqliteSchemaMigrator.EnsureColumns(connection, "mailbox_messages", new[]
            {
                ("idempotency_key", "TEXT"),
                ("request_hash", "TEXT NOT NULL DEFAULT ''"),
                ("unlimited_flag", "INTEGER NOT NULL DEFAULT 0"),
            });
            SqliteSchemaMigrator.EnsureColumns(connection, "mailbox_recipients", new[]
            {
                ("saved_flag", "INTEGER NOT NULL DEFAULT 0"),
                ("saved_at", "TEXT"),
            });
            SqliteSchemaMigrator.EnsureColumns(connection, "mailbox_attachments", new[]
            {
                ("item_core", "BLOB"),
                ("detail_json", "TEXT NOT NULL DEFAULT ''"),
            });
            SqliteSchemaMigrator.EnsureColumns(connection, "mailbox_campaigns", new[]
            {
                ("max_character_id", "INTEGER NOT NULL DEFAULT 0"),
            });

            ExecuteBatch(connection, @"
UPDATE mailbox_messages
SET unlimited_flag = 1
WHERE unlimited_flag = 0
  AND mail_type != 0
  AND expire_at >= '9999-01-01 00:00:00';

DROP INDEX IF EXISTS idx_mailbox_messages_expiry;
CREATE INDEX idx_mailbox_messages_expiry
    ON mailbox_messages(unlimited_flag, expire_at, message_id);
CREATE INDEX IF NOT EXISTS idx_mailbox_recipients_character_state
    ON mailbox_recipients(character_id, folder, saved_flag, deleted_flag, created_at);
CREATE UNIQUE INDEX IF NOT EXISTS ux_mailbox_messages_idempotency
    ON mailbox_messages(sender_character_id, idempotency_key)
    WHERE idempotency_key IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_mailbox_attachments_message_ordinal
    ON mailbox_attachments(message_id, ordinal);

CREATE TRIGGER IF NOT EXISTS trg_mailbox_messages_validate_insert
BEFORE INSERT ON mailbox_messages
WHEN NEW.gold < 0 OR NEW.fee_gold < 0
BEGIN
    SELECT RAISE(ABORT, 'invalid mailbox money');
END;
CREATE TRIGGER IF NOT EXISTS trg_mailbox_messages_validate_update
BEFORE UPDATE OF gold, fee_gold ON mailbox_messages
WHEN NEW.gold < 0 OR NEW.fee_gold < 0
BEGIN
    SELECT RAISE(ABORT, 'invalid mailbox money');
END;
CREATE TRIGGER IF NOT EXISTS trg_mailbox_attachments_validate_insert
BEFORE INSERT ON mailbox_attachments
WHEN NEW.item_template_id <= 0 OR NEW.item_count <= 0 OR NEW.claimed_flag NOT IN (0, 1, 2)
BEGIN
    SELECT RAISE(ABORT, 'invalid mailbox attachment');
END;
CREATE TRIGGER IF NOT EXISTS trg_mailbox_attachments_validate_update
BEFORE UPDATE OF item_template_id, item_count, claimed_flag ON mailbox_attachments
WHEN NEW.item_template_id <= 0 OR NEW.item_count <= 0 OR NEW.claimed_flag NOT IN (0, 1, 2)
BEGIN
    SELECT RAISE(ABORT, 'invalid mailbox attachment');
END;");
        }

        private static void MigrateMailboxSenderSnapshot(SqliteConnection connection)
        {
            if (!TableSqlContains(connection, "mailbox_messages", "FOREIGN KEY (sender_character_id)"))
                return;

            bool foreignKeysEnabled;
            using (var command = new SqliteCommand("PRAGMA foreign_keys;", connection))
                foreignKeysEnabled = Convert.ToInt32(command.ExecuteScalar()) != 0;
            if (foreignKeysEnabled)
                ExecuteBatch(connection, "PRAGMA foreign_keys=OFF;");

            try
            {
                using (var transaction = connection.BeginTransaction())
                {
                    ExecuteBatch(connection, transaction, @"
DROP TABLE IF EXISTS mailbox_messages_v41;
CREATE TABLE mailbox_messages_v41 (
    message_id INTEGER PRIMARY KEY AUTOINCREMENT,
    sender_character_id INTEGER NOT NULL,
    sender_account_id INTEGER NOT NULL DEFAULT 0,
    sender_name TEXT NOT NULL DEFAULT '',
    receiver_character_id INTEGER NOT NULL,
    receiver_account_id INTEGER NOT NULL DEFAULT 0,
    receiver_name TEXT NOT NULL DEFAULT '',
    title TEXT NOT NULL DEFAULT '',
    body TEXT NOT NULL DEFAULT '',
    gold INTEGER NOT NULL DEFAULT 0 CHECK(gold >= 0),
    fee_gold INTEGER NOT NULL DEFAULT 0 CHECK(fee_gold >= 0),
    mail_type INTEGER NOT NULL DEFAULT 0,
    source_protocol INTEGER NOT NULL DEFAULT 0,
    idempotency_key TEXT,
    request_hash TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    unlimited_flag INTEGER NOT NULL DEFAULT 0 CHECK(unlimited_flag IN (0, 1)),
    expire_at TEXT NOT NULL,
    deleted_by_sender INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (receiver_character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);
INSERT INTO mailbox_messages_v41 (
    message_id, sender_character_id, sender_account_id, sender_name,
    receiver_character_id, receiver_account_id, receiver_name,
    title, body, gold, fee_gold, mail_type, source_protocol,
    idempotency_key, request_hash, created_at, unlimited_flag,
    expire_at, deleted_by_sender
)
SELECT
    message_id, sender_character_id, sender_account_id, sender_name,
    receiver_character_id, receiver_account_id, receiver_name,
    title, body, gold, fee_gold, mail_type, source_protocol,
    idempotency_key, request_hash, created_at, unlimited_flag,
    expire_at, deleted_by_sender
FROM mailbox_messages;
DROP TABLE mailbox_messages;
ALTER TABLE mailbox_messages_v41 RENAME TO mailbox_messages;

CREATE INDEX idx_mailbox_messages_receiver_created
    ON mailbox_messages(receiver_character_id, created_at);
CREATE INDEX idx_mailbox_messages_sender_created
    ON mailbox_messages(sender_character_id, created_at);
CREATE INDEX idx_mailbox_messages_expiry
    ON mailbox_messages(unlimited_flag, expire_at, message_id);
CREATE UNIQUE INDEX ux_mailbox_messages_idempotency
    ON mailbox_messages(sender_character_id, idempotency_key)
    WHERE idempotency_key IS NOT NULL;

CREATE TRIGGER trg_mailbox_messages_validate_insert
BEFORE INSERT ON mailbox_messages
WHEN NEW.gold < 0 OR NEW.fee_gold < 0
BEGIN
    SELECT RAISE(ABORT, 'invalid mailbox money');
END;
CREATE TRIGGER trg_mailbox_messages_validate_update
BEFORE UPDATE OF gold, fee_gold ON mailbox_messages
WHEN NEW.gold < 0 OR NEW.fee_gold < 0
BEGIN
    SELECT RAISE(ABORT, 'invalid mailbox money');
END;");
                    transaction.Commit();
                }
            }
            finally
            {
                if (foreignKeysEnabled)
                    ExecuteBatch(connection, "PRAGMA foreign_keys=ON;");
            }
        }

        private static void MigrateQuestProgressConcurrency(SqliteConnection connection)
        {
            SqliteSchemaMigrator.EnsureColumns(
                connection,
                "character_active_quests",
                new[]
                {
                    ("version", "INTEGER NOT NULL DEFAULT 0"),
                });
            ExecuteBatch(connection, @"
DELETE FROM character_active_quests
WHERE rowid NOT IN (
    SELECT MIN(rowid)
    FROM character_active_quests
    GROUP BY character_id, quest_id
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_character_active_quests_identity
    ON character_active_quests(character_id, quest_id);
CREATE TABLE IF NOT EXISTS quest_progress_event_inbox (
    character_id INTEGER NOT NULL,
    event_id TEXT NOT NULL,
    event_kind TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (character_id, event_id, event_kind),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);");
        }

        private static void MigrateQuestActivationIdentity(
            SqliteConnection connection)
        {
            SqliteSchemaMigrator.EnsureColumns(
                connection,
                "character_active_quests",
                new[]
                {
                    ("activation_id", "TEXT NOT NULL DEFAULT ''"),
                });

            var inboxHasActivation = HasColumn(
                connection,
                "quest_progress_event_inbox",
                "activation_id");
            using (var transaction = connection.BeginTransaction())
            {
                ExecuteBatch(connection, transaction, @"
UPDATE character_active_quests
SET activation_id = lower(hex(randomblob(16)))
WHERE activation_id IS NULL OR activation_id = '';
CREATE UNIQUE INDEX IF NOT EXISTS uq_character_active_quests_activation
    ON character_active_quests(character_id, activation_id);
DROP TABLE IF EXISTS quest_progress_event_inbox_v50;
CREATE TABLE quest_progress_event_inbox_v50 (
    character_id INTEGER NOT NULL,
    activation_id TEXT NOT NULL,
    event_id TEXT NOT NULL,
    event_kind TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (character_id, activation_id, event_id, event_kind),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);");

                ExecuteBatch(
                    connection,
                    transaction,
                    inboxHasActivation
                        ? @"
INSERT OR IGNORE INTO quest_progress_event_inbox_v50
    (character_id, activation_id, event_id, event_kind, created_at)
SELECT character_id, activation_id, event_id, event_kind, created_at
FROM quest_progress_event_inbox
WHERE activation_id IS NOT NULL AND activation_id <> '';"
                        : @"
INSERT OR IGNORE INTO quest_progress_event_inbox_v50
    (character_id, activation_id, event_id, event_kind, created_at)
SELECT inbox.character_id,
       active.activation_id,
       inbox.event_id,
       inbox.event_kind,
       inbox.created_at
FROM quest_progress_event_inbox AS inbox
JOIN character_active_quests AS active
  ON active.character_id = inbox.character_id;");

                ExecuteBatch(connection, transaction, @"
DROP TABLE quest_progress_event_inbox;
ALTER TABLE quest_progress_event_inbox_v50
    RENAME TO quest_progress_event_inbox;");
                transaction.Commit();
            }
        }

        private static bool HasColumn(
            SqliteConnection connection,
            string tableName,
            string columnName)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA table_info({tableName});";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (string.Equals(
                                reader.GetString(1),
                                columnName,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static void MigrateKnightShieldDeck(SqliteConnection connection)
        {
            ExecuteBatch(connection, @"
CREATE TABLE IF NOT EXISTS character_knight_shield_deck (
    character_id INTEGER NOT NULL,
    slot_index INTEGER NOT NULL CHECK (slot_index >= 0 AND slot_index <= 4),
    shield_item_id INTEGER NOT NULL CHECK (shield_item_id > 0),
    PRIMARY KEY (character_id, slot_index),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);");
        }

        private static void EnsureInventoryAuditLogV2(SqliteConnection connection)
        {
            ExecuteBatch(connection, @"
CREATE TABLE IF NOT EXISTS inventory_audit_log_v2 (
    audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    session_id TEXT,
    owner_scope TEXT NOT NULL DEFAULT 'character' CHECK(owner_scope IN ('character', 'account')),
    owner_id INTEGER NOT NULL DEFAULT 0,
    character_id INTEGER NOT NULL DEFAULT 0,
    account_id INTEGER NOT NULL DEFAULT 0,
    action_name TEXT NOT NULL,
    list_type INTEGER,
    slot_index INTEGER,
    item_id INTEGER NOT NULL DEFAULT 0,
    item_kind INTEGER NOT NULL DEFAULT 0,
    value_before INTEGER NOT NULL DEFAULT 0,
    value_after INTEGER NOT NULL DEFAULT 0,
    count_before INTEGER NOT NULL DEFAULT 0,
    count_after INTEGER NOT NULL DEFAULT 0,
    count_delta INTEGER NOT NULL DEFAULT 0,
    before_core_hash TEXT,
    after_core_hash TEXT,
    payload_json TEXT NOT NULL DEFAULT '{}'
);

CREATE INDEX IF NOT EXISTS idx_inventory_audit_v2_char_time
    ON inventory_audit_log_v2(character_id, created_at);

CREATE INDEX IF NOT EXISTS idx_inventory_audit_v2_account_time
    ON inventory_audit_log_v2(account_id, created_at);

CREATE INDEX IF NOT EXISTS idx_inventory_audit_v2_action_time
    ON inventory_audit_log_v2(action_name, created_at);");
        }

        private static void MigrateSkillTreeIndexDefault(SqliteConnection connection)
        {
            bool foreignKeysEnabled;
            using (var command = new SqliteCommand("PRAGMA foreign_keys;", connection))
                foreignKeysEnabled = Convert.ToInt32(command.ExecuteScalar()) != 0;
            if (foreignKeysEnabled)
                ExecuteBatch(connection, "PRAGMA foreign_keys=OFF;");

            try
            {
                using (var transaction = connection.BeginTransaction())
                {
                    ExecuteBatch(connection, transaction, @"
ALTER TABLE character_subtype1_fields RENAME TO character_subtype1_fields_v24;
CREATE TABLE character_subtype1_fields (
    character_id INTEGER PRIMARY KEY,
    stat_hp_max INTEGER NOT NULL DEFAULT 0,
    stat_mp_max INTEGER NOT NULL DEFAULT 0,
    stat_physical_attack INTEGER NOT NULL DEFAULT 0,
    stat_physical_defense INTEGER NOT NULL DEFAULT 0,
    stat_magical_attack INTEGER NOT NULL DEFAULT 0,
    stat_magical_defense INTEGER NOT NULL DEFAULT 0,
    stat_fire_resistance INTEGER NOT NULL DEFAULT 0,
    stat_water_resistance INTEGER NOT NULL DEFAULT 0,
    stat_dark_resistance INTEGER NOT NULL DEFAULT 0,
    stat_light_resistance INTEGER NOT NULL DEFAULT 0,
    stat_inventory_limit INTEGER NOT NULL DEFAULT 0,
    stat_hp_regen_speed INTEGER NOT NULL DEFAULT 0,
    stat_mp_regen_speed INTEGER NOT NULL DEFAULT 0,
    stat_move_speed INTEGER NOT NULL DEFAULT 0,
    stat_attack_speed INTEGER NOT NULL DEFAULT 0,
    stat_cast_speed INTEGER NOT NULL DEFAULT 0,
    stat_hit_recovery INTEGER NOT NULL DEFAULT 0,
    stat_jump_power INTEGER NOT NULL DEFAULT 0,
    stat_weight INTEGER NOT NULL DEFAULT 0,
    stat_level INTEGER NOT NULL DEFAULT 0,
    name_tag_item_id INTEGER NOT NULL DEFAULT 0,
    name_tag_expire_time INTEGER NOT NULL DEFAULT 0,
    skill_tree_index INTEGER NOT NULL DEFAULT -1,
    equipped_creature_level INTEGER NOT NULL DEFAULT 0,
    equip_list_trailing INTEGER NOT NULL DEFAULT 0,
    manage_level INTEGER NOT NULL DEFAULT 0,
    flag_byte INTEGER NOT NULL DEFAULT 0,
    guild_power_war INTEGER NOT NULL DEFAULT 0,
    server_timestamp INTEGER NOT NULL DEFAULT 0,
    quest_shop_count INTEGER NOT NULL DEFAULT 0,
    progress1 INTEGER NOT NULL DEFAULT 0,
    progress2 INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);
INSERT INTO character_subtype1_fields (
    character_id, stat_hp_max, stat_mp_max, stat_physical_attack, stat_physical_defense,
    stat_magical_attack, stat_magical_defense, stat_fire_resistance, stat_water_resistance,
    stat_dark_resistance, stat_light_resistance, stat_inventory_limit,
    stat_hp_regen_speed, stat_mp_regen_speed, stat_move_speed, stat_attack_speed,
    stat_cast_speed, stat_hit_recovery, stat_jump_power, stat_weight, stat_level,
    name_tag_item_id, name_tag_expire_time, skill_tree_index, equipped_creature_level,
    equip_list_trailing, manage_level, flag_byte, guild_power_war, server_timestamp,
    quest_shop_count, progress1, progress2
)
SELECT
    character_id, stat_hp_max, stat_mp_max, stat_physical_attack, stat_physical_defense,
    stat_magical_attack, stat_magical_defense, stat_fire_resistance, stat_water_resistance,
    stat_dark_resistance, stat_light_resistance, stat_inventory_limit,
    stat_hp_regen_speed, stat_mp_regen_speed, stat_move_speed, stat_attack_speed,
    stat_cast_speed, stat_hit_recovery, stat_jump_power, stat_weight, stat_level,
    name_tag_item_id, name_tag_expire_time,
    CASE WHEN skill_tree_index IN (0, 1) THEN skill_tree_index ELSE -1 END,
    equipped_creature_level, equip_list_trailing, manage_level, flag_byte, guild_power_war,
    server_timestamp, quest_shop_count, progress1, progress2
FROM character_subtype1_fields_v24;
DROP TABLE character_subtype1_fields_v24;");
                    transaction.Commit();
                }
            }
            finally
            {
                if (foreignKeysEnabled)
                    ExecuteBatch(connection, "PRAGMA foreign_keys=ON;");
            }
        }

        private static void MigrateSkillPointDerivation(SqliteConnection connection)
        {
            bool foreignKeysEnabled;
            using (var cmd = new SqliteCommand("PRAGMA foreign_keys;", connection))
                foreignKeysEnabled = Convert.ToInt32(cmd.ExecuteScalar()) != 0;
            if (foreignKeysEnabled)
                ExecuteBatch(connection, "PRAGMA foreign_keys=OFF;");

            try
            {
                using (var tx = connection.BeginTransaction())
                {
                    ExecuteBatch(connection, @"
DROP TABLE IF EXISTS character_skill_points;
DROP TABLE IF EXISTS character_skill_tail;
DROP TABLE IF EXISTS character_skills;
CREATE TABLE character_skills (
    character_id INTEGER NOT NULL,
    page_index INTEGER NOT NULL DEFAULT 0,
    slot INTEGER NOT NULL DEFAULT -1,
    skill_id INTEGER NOT NULL DEFAULT 0,
    level INTEGER NOT NULL DEFAULT 0,
    extra_values BLOB,
    PRIMARY KEY (character_id, page_index, slot),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);");
                    tx.Commit();
                }
            }
            finally
            {
                if (foreignKeysEnabled)
                    ExecuteBatch(connection, "PRAGMA foreign_keys=ON;");
            }
        }

        private static bool TableExists(SqliteConnection connection, string tableName)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n;";
                cmd.Parameters.AddWithValue("@n", tableName);
                return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
            }
        }

        private static void RenameTableIfExists(SqliteConnection connection, string oldName, string newName)
        {
            if (!TableExists(connection, oldName))
                return;
            // 同名新表已被 schema 创建时先清掉空壳, 保住旧表数据
            if (TableExists(connection, newName))
                ExecuteBatch(connection, $"DROP TABLE {newName};");
            ExecuteBatch(connection, $"ALTER TABLE {oldName} RENAME TO {newName};");
        }

        private static bool ColumnExists(SqliteConnection connection, string tableName, string columnName)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info({tableName});";
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            return false;
        }

        private static void MigrateAvatarDetailIndependentUid(SqliteConnection connection)
        {
            const string tableName = "character_avatar_detail";
            if (!TableExists(connection, tableName))
            {
                ExecuteBatch(connection, @"
CREATE TABLE IF NOT EXISTS character_avatar_detail (
    item_uid INTEGER PRIMARY KEY,
    owner_id INTEGER NOT NULL DEFAULT 0,
    character_id INTEGER NOT NULL DEFAULT 0,
    item_id INTEGER NOT NULL DEFAULT 0,
    expire_date INTEGER NOT NULL DEFAULT 0,
    clear_avatar_id INTEGER NOT NULL DEFAULT 0,
    jewel_socket BLOB NOT NULL CHECK(length(jewel_socket) = 30),
    color1 INTEGER NOT NULL DEFAULT 0,
    color2 INTEGER NOT NULL DEFAULT 0,
    delete_date INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_character_avatar_detail_character
    ON character_avatar_detail(character_id);");
                return;
            }

            if (!TableSqlContains(connection, tableName, "REFERENCES character_new_items"))
                return;

            bool foreignKeysEnabled;
            using (var command = new SqliteCommand("PRAGMA foreign_keys;", connection))
                foreignKeysEnabled = Convert.ToInt32(command.ExecuteScalar()) != 0;
            if (foreignKeysEnabled)
                ExecuteBatch(connection, "PRAGMA foreign_keys=OFF;");

            try
            {
                using (var transaction = connection.BeginTransaction())
                {
                    ExecuteBatch(connection, transaction, @"
CREATE TABLE IF NOT EXISTS character_avatar_detail_v31 (
    item_uid INTEGER PRIMARY KEY,
    owner_id INTEGER NOT NULL DEFAULT 0,
    character_id INTEGER NOT NULL DEFAULT 0,
    item_id INTEGER NOT NULL DEFAULT 0,
    expire_date INTEGER NOT NULL DEFAULT 0,
    clear_avatar_id INTEGER NOT NULL DEFAULT 0,
    jewel_socket BLOB NOT NULL CHECK(length(jewel_socket) = 30),
    color1 INTEGER NOT NULL DEFAULT 0,
    color2 INTEGER NOT NULL DEFAULT 0,
    delete_date INTEGER NOT NULL DEFAULT 0
);
INSERT OR REPLACE INTO character_avatar_detail_v31 (
    item_uid, owner_id, character_id, item_id, expire_date, clear_avatar_id,
    jewel_socket, color1, color2, delete_date
)
SELECT item_uid, owner_id, character_id, item_id, expire_date, clear_avatar_id,
       jewel_socket, color1, color2, delete_date
FROM character_avatar_detail;
DROP TABLE character_avatar_detail;
ALTER TABLE character_avatar_detail_v31 RENAME TO character_avatar_detail;
CREATE INDEX IF NOT EXISTS idx_character_avatar_detail_character
    ON character_avatar_detail(character_id);");
                    transaction.Commit();
                }
            }
            finally
            {
                if (foreignKeysEnabled)
                    ExecuteBatch(connection, "PRAGMA foreign_keys=ON;");
            }
        }

        private static void NormalizeLegacyCharmItemKind(SqliteConnection connection)
        {
            using (var transaction = connection.BeginTransaction())
            {
                NormalizeLegacyCharmItemKind(connection, transaction, "character_new_items");
                NormalizeLegacyCharmItemKind(connection, transaction, "account_cargo_new_items");
                transaction.Commit();
            }
        }

        private static void NormalizeLegacyCharmItemKind(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string tableName)
        {
            const byte legacyCharmKind = 12;
            if (!TableExists(connection, tableName))
                return;

            var updates = new List<Tuple<long, byte[]>>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"SELECT item_uid, item_core FROM {tableName};";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var data = reader.IsDBNull(1) ? null : (byte[])reader[1];
                        if (data == null || data.Length < ItemCore.Size || data[ItemCore.ItemKindOffset] != legacyCharmKind)
                            continue;

                        var core = ItemCore.FromBytes(data);
                        core.ItemKind = ItemCore.KindEquipment;
                        updates.Add(Tuple.Create(reader.GetInt64(0), core.ToBytes()));
                    }
                }
            }

            foreach (var update in updates)
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = $"UPDATE {tableName} SET item_core = @itemCore, updated_at = CURRENT_TIMESTAMP WHERE item_uid = @itemUid;";
                    command.Parameters.AddWithValue("@itemUid", update.Item1);
                    command.Parameters.AddWithValue("@itemCore", update.Item2);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void EnsureAvatarUidSequence(SqliteConnection connection)
        {
            using (var transaction = connection.BeginTransaction())
            {
                AvatarDetailRepository.EnsureAvatarUidSequence(connection, transaction);
                transaction.Commit();
            }
        }

        private static void EnsureCreatureUidSequence(SqliteConnection connection)
        {
            using (var transaction = connection.BeginTransaction())
            {
                CreatureDetailRepository.EnsureCreatureUidSequence(connection, transaction);
                transaction.Commit();
            }
        }

        private static bool IsPrimaryKeyColumn(SqliteConnection connection, string tableName, string columnName)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info({tableName});";
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                            return reader.GetInt32(5) > 0;
                    }
                }
            }
            return false;
        }

        private static bool TableSqlContains(SqliteConnection connection, string tableName, string fragment)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=@n;";
                cmd.Parameters.AddWithValue("@n", tableName);
                var sql = cmd.ExecuteScalar() as string;
                return sql != null && sql.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        private static void ExecuteBatch(SqliteConnection connection, string sql)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        private static void ExecuteBatch(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sql)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        public static void Apply(SqliteConnection connection)
        {
            long current = ReadUserVersion(connection);
            foreach (var (version, name, apply) in Steps)
            {
                if (version <= current)
                    continue;

                apply(connection);
                SetUserVersion(connection, version);
                FileLogger.Log($"[Db] migration v{version} applied: {name}");
            }
        }

        private static long ReadUserVersion(SqliteConnection connection)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA user_version;";
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
        }

        private static void SetUserVersion(SqliteConnection connection, int version)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA user_version = {version};";
                cmd.ExecuteNonQuery();
            }
        }
    }
}
