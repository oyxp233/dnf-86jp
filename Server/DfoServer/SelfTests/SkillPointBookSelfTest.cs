using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class SkillPointBookSelfTest
    {
        private const int AccountId = 939031;
        private const int SpCharacterId = 939131;
        private const int TpCharacterId = 939132;
        private const short SpBookSlot = 3;
        private const short TpBookSlot = 4;
        private const int Sp5BookItemId = 1031;
        private const int Tp5BookItemId = 1205;
        private static int _failures;

        public static int Run()
        {
            _failures = 0;
            Console.WriteLine("=== SKILL_POINT_BOOK selftest ===");
            Check("PVF item 1031 resolves to an SP+5 book",
                IsExpectedBook(SkillPointBookDataProvider.Resolve(1031), 5));
            Check("PVF item 1038 resolves to an SP+20 book",
                IsExpectedBook(SkillPointBookDataProvider.Resolve(1038), 20));
            Check("PVF test item 80003 resolves to an SP+5 book",
                IsExpectedBook(SkillPointBookDataProvider.Resolve(80003), 5));
            Check("PVF item 1204 resolves to a TP+1 book",
                IsExpectedBook(SkillPointBookDataProvider.Resolve(1204), 0, 1, 50));
            Check("PVF item 1205 resolves to a TP+5 book",
                IsExpectedBook(SkillPointBookDataProvider.Resolve(1205), 0, 5, 50));
            Check("unrelated item is not classified as a skill-point book",
                !SkillPointBookDataProvider.Resolve(42).IsSkillPointBook);

            var previousDatabasePath = Environment.GetEnvironmentVariable(
                "INVENTORY_DATABASE_PATH");
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "skill-point-book-selftest.db");
            DeleteDatabase(databasePath);
            Environment.SetEnvironmentVariable("INVENTORY_DATABASE_PATH", databasePath);
            var spSessionId = Guid.NewGuid();
            var tpSessionId = Guid.NewGuid();
            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                SeedCharacter(connectionString);

                var spInventory = CreateInventoryWithBook(
                    SpCharacterId, SpBookSlot, Sp5BookItemId, 2);
                var tpInventory = CreateInventoryWithBook(
                    TpCharacterId, TpBookSlot, Tp5BookItemId, 2);
                var spLease = InventoryContext.Register(spSessionId, spInventory);
                var tpLease = InventoryContext.Register(tpSessionId, tpInventory);
                PersistSeededInventory(connectionString, spLease, "SP");
                PersistSeededInventory(connectionString, tpLease, "TP");

                var service = new ExperienceItemUseService(
                    databasePath,
                    ServerPaths.SchemaFilePath,
                    SystemRentalTimeProvider.Instance,
                    new ExperienceItemCooldownTracker());
                var spResult = service.UseBySlot(
                        SpCharacterId,
                        AccountId,
                        InventoryListType.Main,
                        SpBookSlot,
                        ExperienceItemUseLocation.Town);

                Check("using SP+5 book succeeds", spResult.Success);
                Check("result identifies the skill-point path",
                    spResult.IsSkillPointBook
                    && spResult.GrantedSp == 5
                    && spResult.GrantedExp == 0);
                Check("one book is consumed in memory",
                    spInventory.GetItem(InventoryListType.Main, SpBookSlot)?.Count == 1);
                Check("bonus_sp is increased exactly once",
                    ReadInt(connectionString, SpCharacterId, "bonus_sp") == 5);
                Check("consumed stack is committed atomically",
                    ReadPersistedStackCount(
                        connectionString, SpCharacterId, SpBookSlot) == 1);
                Check("both skill pages receive the five-point increase",
                    spResult.SkillPoints.Page0Sp == 5
                    && spResult.SkillPoints.Page1Sp == 5);

                var tpResult = service.UseBySlot(
                    TpCharacterId,
                    AccountId,
                    InventoryListType.Main,
                    TpBookSlot,
                    ExperienceItemUseLocation.Town);
                Check("using TP+5 book succeeds",
                    tpResult.Success
                    && tpResult.IsSkillPointBook
                    && tpResult.GrantedSp == 0
                    && tpResult.GrantedTp == 5);
                Check("TP book increments bonus_tp only",
                    ReadInt(connectionString, TpCharacterId, "bonus_tp") == 5
                    && ReadInt(connectionString, TpCharacterId, "bonus_sp") == 0);
                Check("TP book consumption is committed atomically",
                    tpInventory.GetItem(InventoryListType.Main, TpBookSlot)?.Count == 1
                    && ReadPersistedStackCount(
                        connectionString, TpCharacterId, TpBookSlot) == 1);
            }
            finally
            {
                InventoryContext.Unregister(spSessionId, SpCharacterId);
                InventoryContext.Unregister(tpSessionId, TpCharacterId);
                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    previousDatabasePath);
                DeleteDatabase(databasePath);
            }

            Console.WriteLine(_failures == 0 ? "PASS" : $"FAIL: {_failures}");
            return _failures == 0 ? 0 : 1;
        }

        private static bool IsExpectedBook(
            SkillPointBookDefinition definition,
            int grantedSp)
            => IsExpectedBook(definition, grantedSp, 0, 1);

        private static bool IsExpectedBook(
            SkillPointBookDefinition definition,
            int grantedSp,
            int grantedTp,
            int minimumLevel)
            => definition != null
                && definition.IsSkillPointBook
                && definition.IsSupported
                && definition.GrantedSp == grantedSp
                && definition.GrantedTp == grantedTp
                && definition.MinimumLevel == minimumLevel;

        private static InventoryService CreateInventoryWithBook(
            int characterId,
            short slotIndex,
            int itemId,
            int count)
        {
            var inventory = new InventoryService(characterId, AccountId);
            var item = ItemCore.Create(2, itemId);
            item.Count = count;
            inventory.SetItem(InventoryListType.Main, slotIndex, item);
            return inventory;
        }

        private static void PersistSeededInventory(
            string connectionString,
            InventoryLease lease,
            string label)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    Check($"seeded {label} stack persists",
                        InventoryPersistenceService.SaveDirtyInTransaction(
                            connection,
                            transaction,
                            lease));
                    transaction.Commit();
                }
                lease.Inventory.ClearDirtyState();
            }
        }

        private static void SeedCharacter(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, 'skill-point-book-selftest', '');
INSERT INTO characters (
    character_id, account_id, name, job, level, exp, bonus_sp, bonus_tp)
VALUES
    (@spCid, @aid, 'sp-book-selftest', 0, 1, 0, 0, 0),
    (@tpCid, @aid, 'tp-book-selftest', 0, 50, 0, 0, 0);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@spCid", SpCharacterId);
                    command.Parameters.AddWithValue("@tpCid", TpCharacterId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static int ReadInt(
            string connectionString,
            int characterId,
            string column)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = $"SELECT {column} FROM characters WHERE character_id=@cid;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    return Convert.ToInt32(command.ExecuteScalar());
                }
            }
        }

        private static int ReadPersistedStackCount(
            string connectionString,
            int characterId,
            short slotIndex)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT item_core
FROM character_new_items
WHERE character_id=@cid AND list_type=@listType AND slot_index=@slot;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@listType", 0);
                    command.Parameters.AddWithValue("@slot", slotIndex);
                    return command.ExecuteScalar() is byte[] data
                        ? ItemCore.FromBytes(data).Count
                        : -1;
                }
            }
        }

        private static void DeleteDatabase(string databasePath)
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                try
                {
                    var path = databasePath + suffix;
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                }
            }
        }

        private static void Check(string label, bool success)
        {
            Console.WriteLine($"  [{(success ? "PASS" : "FAIL")}] {label}");
            if (!success)
                _failures++;
        }
    }
}
