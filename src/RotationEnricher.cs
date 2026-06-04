using System;
using System.Collections.Generic;
using System.Linq;
using DamageMeter;
using DamageMeter.TeraDpsApi;
using Data;
using Tera.Game;

// the injected call lives in DamageMeter.dll, so it must be allowed to reach our internal Enrich
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DamageMeter")]

namespace ShinraRotationPatch
{
    // Injected into DamageMeter.DataExporter.AutomatedExport(NpcEntity, AbnormalityStorage),
    // right after the "stats == null" guard. Fills Members.dealtSkillLog with the chronological
    // hit-by-hit dealt-skill timeline, mirroring DamageMeter.Exporter.JsonExporter.JsonSave so the
    // upload POST (EncounterBase serialization) carries the rotation. Never throws into the caller.
    public static class RotationEnricher
    {
        internal static void Enrich(ExtendedStats stats)
        {
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

                    var dealt = stats.AllSkills.GetSkillsDealt(player.User, null, true);
                    foreach (var sk in dealt.OrderBy(x => x.Time))
                    {
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
