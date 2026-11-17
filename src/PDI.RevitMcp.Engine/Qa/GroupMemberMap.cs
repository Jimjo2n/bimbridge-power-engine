// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using PDI.RevitMcp.Protocol;

namespace PDI.RevitMcp.Engine.Qa
{
    public static class GroupMemberMap
    {
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
                        throw new QueryException("miembros: se requieren IDs y UID únicos en cada instantánea");
                    result.Add((id, uid, Arg.GetString(data, "firma")));
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
                ["correspondencia"] = pairing.OrderBy(x => x.Key).Select(x => (object)new object[] { x.Key, x.Value }).ToList(),
                ["anterior_sin_correspondencia"] = unmapped, ["actual_sin_correspondencia"] = unmatched,
                ["completo"] = unmapped.Count == 0 && unmatched.Count == 0,
            };
        }
    }
}
