using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.SelfTests
{
    // 覆盖任务列表过滤和服务端直接接取校验，避免只修复客户端显示。
    internal static class SpecialProfessionQuestPolicySelfTest
    {
        internal static int Run()
        {
            Console.WriteLine("=== SPECIAL_PROFESSION_QUEST_POLICY selftest ===");
            var failures = 0;

            foreach (var questType in new[] { 1, 2, 3 })
            {
                var quest = new QuestFile
                {
                    JobChangeQuestValue = questType,
                };
                Check(
                    $"dark knight blocks profession quest type {questType}",
                    QuestData.IsProfessionQuestBlockedForJob(quest, 9),
                    ref failures);
                Check(
                    $"creator blocks profession quest type {questType}",
                    QuestData.IsProfessionQuestBlockedForJob(quest, 10),
                    ref failures);
                Check(
                    $"normal job keeps profession quest type {questType}",
                    !QuestData.IsProfessionQuestBlockedForJob(quest, 0),
                    ref failures);
            }

            CheckAllowedType(0, "ordinary quest", ref failures);
            CheckAllowedType(10, "pet evolution quest", ref failures);
            CheckAllowedType(20, "expert-job quest", ref failures);

            CheckAcceptableList(9, "dark knight", ref failures);
            CheckAcceptableList(10, "creator", ref failures);
            CheckDirectAcceptance(9, "dark knight", ref failures);
            CheckDirectAcceptance(10, "creator", ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void CheckAllowedType(
            int questType,
            string label,
            ref int failures)
        {
            var quest = new QuestFile
            {
                JobChangeQuestValue = questType,
            };
            Check(
                $"special professions keep {label}",
                !QuestData.IsProfessionQuestBlockedForJob(quest, 9)
                && !QuestData.IsProfessionQuestBlockedForJob(quest, 10),
                ref failures);
        }

        private static void CheckAcceptableList(
            int characterJob,
            string label,
            ref int failures)
        {
            var acceptable = QuestData.ComputeAcceptableQuests(
                70,
                characterJob,
                0,
                new HashSet<int>(),
                new Dictionary<int, int>());
            var blocked = acceptable
                .Where(questId => QuestData.IsProfessionQuestBlockedForJob(
                    questId,
                    characterJob))
                .ToArray();
            Check(
                $"{label} acceptable list excludes profession quests",
                blocked.Length == 0,
                ref failures);
        }

        private static void CheckDirectAcceptance(
            byte characterJob,
            string label,
            ref int failures)
        {
            const ushort professionQuestId = 4065;
            var characterId = 193000 + characterJob;
            var accountId = characterId;
            var tempDir = Path.Combine(Path.GetTempPath(), "DfoServerSelfTests");
            Directory.CreateDirectory(tempDir);
            var databasePath = Path.Combine(
                tempDir,
                $"special-profession-quest-{characterJob}.db");
            DeleteDatabase(databasePath);
            var connectionString = SqliteDatabaseBootstrap.Initialize(
                databasePath,
                ServerPaths.SchemaFilePath);

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @mid, '');";
                    command.Parameters.AddWithValue("@aid", accountId);
                    command.Parameters.AddWithValue(
                        "@mid",
                        $"special-profession-{characterJob}");
                    command.ExecuteNonQuery();
                }
            }

            new SqliteCharacterRepository(databasePath, ServerPaths.SchemaFilePath)
                .Create(new CharacterRecord
                {
                    CharacterId = characterId,
                    AccountId = accountId,
                    Name = Encoding.UTF8.GetBytes(
                        $"special-profession-{characterJob}"),
                    Job = characterJob,
                    GrowType = 0,
                    Level = 70,
                });

            var sessionId = Guid.NewGuid();
            InventoryContext.Register(
                sessionId,
                new InventoryService(characterId, accountId));
            try
            {
                var result = new QuestService(connectionString).HandleAcceptQuest(
                    characterId,
                    BitConverter.GetBytes(professionQuestId),
                    accountId);
                Check(
                    $"{label} direct profession-quest accept is rejected",
                    !result.Success
                    && result.ErrorCode == 21
                    && QuestService.LoadActiveQuests(
                        connectionString,
                        characterId).Count == 0,
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(sessionId, characterId);
            }
        }

        private static void DeleteDatabase(string databasePath)
        {
            foreach (var path in new[]
            {
                databasePath,
                databasePath + "-wal",
                databasePath + "-shm",
            })
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        private static void Check(
            string name,
            bool passed,
            ref int failures)
        {
            Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {name}");
            if (!passed)
                failures++;
        }
    }
}
