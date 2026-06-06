using System;
using System.Collections.Generic;
using DamageMeter;
using DamageMeter.TeraDpsApi;
using Data;
using Tera.Game;

// Compiled against the pass1 DamageMeter (which grants IVT to this assembly so we
// can see internal ExtendedStats). After the build, this whole type is MERGED into
// DamageMeter.dll so there is no separate assembly to load at runtime.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DamageMeter")]

namespace ShinraRotationPatch
{
    // Injected call lives in DamageMeter.DataExporter.AutomatedExport(NpcEntity, AbnormalityStorage),
    // right after the "stats == null" guard. Fills Members.dealtSkillLog with the chronological
    // hit-by-hit dealt-skill timeline, mirroring DamageMeter.Exporter.JsonExporter.JsonSave so the
    // upload POST (EncounterBase serialization) carries the rotation. Never throws into the caller.
    //
    // No LINQ / no lambdas on purpose: the type gets merged into DamageMeter.dll by IL rewriting,
    // and avoiding compiler-generated closures/display-classes keeps that merge simple and reliable.
    public static class RotationEnricher
    {
        // Only this host receives the heavy hit-by-hit dealtSkillLog.
        // Other upload targets strip it to avoid oversized payloads.
        private const string KeepHost = "enragedon";

        // Last stats captured in AutomatedExport, so we can re-fill the log per server.
        internal static ExtendedStats _last;

        // Called at the start of DpsServer.CheckAndSendFightData (injected), once per
        // upload target, BEFORE serialization. Keeps dealtSkillLog only for enragedon.
        internal static void ApplyForServer(DpsServer server, EncounterBase data)
        {
            try
            {
                bool keep = false;
                if (server != null)
                {
                    var url = server.UploadUrl;
                    if (url != null && url.ToString().IndexOf(KeepHost, StringComparison.OrdinalIgnoreCase) >= 0)
                        keep = true;
                }
                if (keep)
                {
                    if (_last != null) Enrich(_last);          // (re)fill for enragedon
                }
                else if (data != null && data.members != null)
                {
                    foreach (Members m in data.members) m.dealtSkillLog = null;  // strip for others
                }
            }
            catch { /* never break the upload */ }
        }

        internal static void Enrich(ExtendedStats stats)
        {
            _last = stats;
            try
            {
                if (stats == null || stats.BaseStats == null || stats.AllSkills == null) return;

                var skillDb = BasicTeraData.Instance.SkillDatabase;
                long firstTick = stats.FirstTick;

                foreach (Members member in stats.BaseStats.members)
                {
                    member.dealtSkillLog = new List<JsonSkill>();

                    Player player = PacketProcessor.Instance.PlayerTracker.Get(member.playerServerId, member.playerId);
                    if (player == null) continue;

                    // collect dealt skills into a list we can sort without LINQ
                    var list = new List<DamageMeter.Database.Structures.Skill>();
                    foreach (var s in stats.AllSkills.GetSkillsDealt(player.User, null, true))
                        list.Add(s);

                    // insertion sort by Time (ascending) -- stable, no lambdas
                    for (int a = 1; a < list.Count; a++)
                    {
                        DamageMeter.Database.Structures.Skill key = list[a];
                        int b = a - 1;
                        while (b >= 0 && list[b].Time > key.Time)
                        {
                            list[b + 1] = list[b];
                            b--;
                        }
                        list[b + 1] = key;
                    }

                    for (int i = 0; i < list.Count; i++)
                    {
                        DamageMeter.Database.Structures.Skill sk = list[i];
                        var js = new JsonSkill
                        {
                            time = (int)((sk.Time - firstTick) / 10000),
                            type = (int)sk.Type,
                            crit = sk.Critic,
                            dot = sk.HotDot,
                            skillId = skillDb.GetSkillByPetName(sk.Pet?.Name, player.RaceGenderClass)?.Id ?? sk.SkillId,
                            amount = sk.Amount.ToString(),
                            target = (sk.Target.Id.Id == ulong.MaxValue) ? null : sk.Target.Id.Id.ToString()
                        };
                        member.dealtSkillLog.Add(js);
                    }
                }
            }
            catch (Exception ex)
            {
                try { BasicTeraData.LogError("[ShinraRotationPatch] Enrich failed: " + ex); }
                catch { /* never let logging break the upload */ }
            }
        }
    }
}
