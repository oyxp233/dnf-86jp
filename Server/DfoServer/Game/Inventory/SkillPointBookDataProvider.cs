using PvfLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Inventory
{
    internal static class SkillPointBookDataProvider
    {
        private sealed class KnownBook
        {
            internal KnownBook(int grantedSp, int grantedTp, int etcVariant)
            {
                GrantedSp = grantedSp;
                GrantedTp = grantedTp;
                EtcVariant = etcVariant;
            }

            internal int GrantedSp { get; }
            internal int GrantedTp { get; }
            internal int EtcVariant { get; }
        }

        // 技能书没有统一的数值标签，只能按已核对过的 PVF 路径建立白名单。
        // 同时继续校验名称、类型和音效标记，防止同名普通道具被误当成 SP/TP 书。
        private static readonly Dictionary<string, KnownBook> KnownBooks
            = new Dictionary<string, KnownBook>(StringComparer.OrdinalIgnoreCase)
            {
                ["book_skill1.stk"] = new KnownBook(5, 0, 8),
                ["book_skill2.stk"] = new KnownBook(20, 0, 1),
                ["extention/test_book_skill1.stk"] = new KnownBook(5, 0, 8),
                ["book_fskill1.stk"] = new KnownBook(0, 1, 8),
                ["book_fskill2.stk"] = new KnownBook(0, 5, 8),
            };

        private static readonly HashSet<string> AllowedRootTags
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "name", "explain", "grade", "attach type", "rarity", "weight",
                "usable job", "minimum level", "maximum level", "stack limit",
                "icon", "field image", "stackable type", "price", "value",
                "move wav", "use wav", "expiration date", "usable period",
            };

        internal static SkillPointBookDefinition Resolve(int itemTemplateId)
            => Resolve(
                itemTemplateId,
                ItemMetadataResolver.GetStackableEntry(itemTemplateId)?.FilePath,
                StackableItemProvider.Load(itemTemplateId));

        internal static SkillPointBookDefinition Resolve(
            int itemTemplateId,
            string filePath,
            StackableItemFile stackable)
        {
            var result = new SkillPointBookDefinition(itemTemplateId);
            if (!KnownBooks.TryGetValue(NormalizePath(filePath), out var known))
                return result;

            result.IsSkillPointBook = true;
            if (stackable?.Root == null)
                return Reject(result, "skill-point book PVF definition is unavailable");

            var expectedName = known.GrantedSp > 0
                ? $"SP+{known.GrantedSp}技能书"
                : $"TP+{known.GrantedTp}技能书";
            if (!string.Equals(
                    NormalizeText(stackable.Name),
                    expectedName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Reject(result, "skill-point book name does not match its reviewed definition");
            }

            if (!IsEtcVariantStackable(stackable.StackableType, known.EtcVariant))
            {
                return Reject(
                    result,
                    $"skill-point book is not a variant-{known.EtcVariant} [etc] stackable");
            }

            if (!string.Equals(
                    NormalizeText(stackable.GetStringValue("use wav")),
                    "SP_UP_ITEM",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Reject(result, "skill-point book is missing the SP_UP_ITEM use marker");
            }

            var effects = stackable.StatusIncreases;
            if ((effects != null && effects.Count > 0)
                || !string.IsNullOrWhiteSpace(stackable.ActionTypeName))
            {
                return Reject(result, "skill-point book is mixed with another stackable behavior");
            }

            var unknownTag = stackable.Root.Children.FirstOrDefault(node =>
                !AllowedRootTags.Contains(node.Tag));
            if (unknownTag != null)
                return Reject(result, $"unreviewed PVF behavior tag [{unknownTag.Tag}]");

            if (!HasAllJobsOnly(stackable))
                return Reject(result, "skill-point book usable-job definition is not [all]");

            if (!TryReadLevelRestrictions(
                    stackable,
                    out var minimumLevel,
                    out var maximumLevel,
                    out var error))
            {
                return Reject(result, error);
            }

            if (!StackableExpirationPolicyResolver.TryResolve(stackable, out var expiration))
                return Reject(result, "invalid expiration definition");

            result.GrantedSp = known.GrantedSp;
            result.GrantedTp = known.GrantedTp;
            result.MinimumLevel = minimumLevel;
            result.MaximumLevel = maximumLevel;
            result.AbsoluteExpirationUnixTime = expiration.AbsoluteExpirationUnixTime;
            result.UsablePeriodDays = expiration.UsablePeriodDays;
            result.IsSupported = true;
            return result;
        }

        private static bool TryReadLevelRestrictions(
            StackableItemFile stackable,
            out int minimumLevel,
            out int maximumLevel,
            out string error)
        {
            minimumLevel = -1;
            maximumLevel = -1;
            error = null;
            if (!StackablePvfValueReader.TryReadOptionalNonNegativeInt(
                    stackable,
                    "minimum level",
                    out var hasMinimum,
                    out var minimum)
                || !StackablePvfValueReader.TryReadOptionalNonNegativeInt(
                    stackable,
                    "maximum level",
                    out var hasMaximum,
                    out var maximum))
            {
                error = "invalid level restriction definition";
                return false;
            }

            minimumLevel = hasMinimum ? minimum : -1;
            maximumLevel = hasMaximum ? maximum : -1;
            if (!hasMinimum || !hasMaximum || minimum <= maximum)
                return true;

            error = "minimum level exceeds maximum level";
            return false;
        }

        private static bool HasAllJobsOnly(StackableItemFile stackable)
        {
            var nodes = stackable.Root.GetChildren("usable job");
            if (nodes.Count != 1 || nodes[0].Children.Count != 0)
                return false;

            var jobs = new List<string>();
            foreach (var item in nodes[0].DataItems)
            {
                foreach (Match match in Regex.Matches(
                             item.GetContent(stackable.Content) ?? string.Empty,
                             @"\[(?<job>[^\]]+)\]"))
                {
                    var job = NormalizeText(match.Groups["job"].Value);
                    if (job.Length > 0)
                        jobs.Add(job);
                }
            }

            return jobs.Count == 1
                && string.Equals(jobs[0], "all", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsEtcVariantStackable(string raw, int expectedVariant)
        {
            var match = Regex.Match(
                raw ?? string.Empty,
                @"^\s*`?\[(?<type>[^\]]+)\]`?\s+(?<variant>-?\d+)\s*$");
            return match.Success
                && string.Equals(
                    match.Groups["type"].Value.Trim(),
                    "etc",
                    StringComparison.OrdinalIgnoreCase)
                && int.TryParse(match.Groups["variant"].Value, out var variant)
                && variant == expectedVariant;
        }

        private static string NormalizePath(string raw)
            => (raw ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');

        private static string NormalizeText(string raw)
            => (raw ?? string.Empty).Trim().Trim('`').Trim();

        private static SkillPointBookDefinition Reject(
            SkillPointBookDefinition result,
            string reason)
        {
            result.IsSupported = false;
            result.UnsupportedReason = reason;
            return result;
        }
    }
}
