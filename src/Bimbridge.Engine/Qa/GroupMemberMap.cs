// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Bimbridge.Protocol;

namespace Bimbridge.Engine.Qa
{
    public static class GroupMemberMap
    {
        public static bool MatchesVariant(Dictionary<string, object> before, Dictionary<string, object> after,
            Dictionary<string, object> changedTypes)
        {
            if (before.Count != after.Count) return false;
            foreach (var member in before)
            {
                if (!after.TryGetValue(member.Key, out var current)) return false;
                var expected = new Dictionary<string, object>((Dictionary<string, object>)member.Value);
                if (changedTypes.TryGetValue(member.Key, out var type)) expected["type"] = type;
                if (OpSemantics.Canonical(expected) != OpSemantics.Canonical(current)) return false;
            }
            return true;
        }

        public static Dictionary<string, object> Compare(List<object> before, List<object> after)
        {
            static List<(long Id, string Uid, string Signature)> Read(List<object> rows)
            {
                var result = new List<(long, string, string)>();
                var ids = new HashSet<long>();
                var uids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in rows)
                {
                    if (!(row is Dictionary<string, object> data) || !(Arg.GetLong(data, "id") is long id) || id <= 0 ||
                        !ids.Add(id) || !(Arg.GetString(data, "uid") is string uid) || uid.Length == 0 || !uids.Add(uid))
                        throw new QueryException("members: unique IDs and UIDs are required in each snapshot");
                    result.Add((id, uid, Arg.GetString(data, "signature")));
                }
                return result;
            }
            var old = Read(before);
            var newer = Read(after);
            var byUid = newer.ToDictionary(x => x.Uid, StringComparer.Ordinal);
            var pairing = new Dictionary<long, long>();
            var used = new HashSet<long>();
            foreach (var row in old)
                if (byUid.TryGetValue(row.Uid, out var same))
                {
                    pairing[row.Id] = same.Id;
                    used.Add(same.Id);
                }
            var available = newer.Where(x => !used.Contains(x.Id) && x.Signature != null)
                .ToLookup(x => x.Signature, StringComparer.Ordinal);
            foreach (var slot in old.Where(x => !pairing.ContainsKey(x.Id) && x.Signature != null)
                .GroupBy(x => x.Signature, StringComparer.Ordinal))
            {
                if (slot.Count() != 1 || available[slot.Key].Count() != 1) continue;
                var successor = available[slot.Key].Single().Id;
                pairing[slot.Single().Id] = successor;
                used.Add(successor);
            }
            var unmapped = old.Where(x => !pairing.ContainsKey(x.Id)).Select(x => (object)x.Id).ToList();
            var unmatched = newer.Where(x => !used.Contains(x.Id)).Select(x => (object)x.Id).ToList();
            return new Dictionary<string, object>
            {
                ["correspondence"] = pairing.OrderBy(x => x.Key).Select(x => (object)new object[] { x.Key, x.Value }).ToList(),
                ["previous_without_match"] = unmapped, ["current_without_match"] = unmatched,
                ["complete"] = unmapped.Count == 0 && unmatched.Count == 0,
            };
        }
    }
}
