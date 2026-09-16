using System;
using System.Collections.Generic;
using DamageMeter;
using DamageMeter.TeraDpsApi;
using Data;
using Tera.Game;

// Compiled against the pass1 DamageMeter (which grants IVT to this assembly so we can see
// internal ExtendedStats and the internal Members.rotBackup field pass1 adds). After the
// build, this whole type is MERGED into DamageMeter.dll so there is no separate assembly
// to load at runtime.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DamageMeter")]

namespace ShinraRotationPatch
{
    // =================================================================================
    // THREAD CONTRACT -- read this before changing anything here. It is the whole point.
    // =================================================================================
    // ShinraMeter has exactly two threads that matter to us:
    //
    //   (1) the packet-processing thread: runs S_DESPAWN_NPC -> DataExporter.AutomatedExport,
    //       and is the ONLY thread stock code ever uses to touch PlayerTracker,
    //       AbnormalityStorage or the live entity/player state. Verified exhaustively
    //       against the decompiled meter: every PlayerTracker call site in the product
    //       (Database.PlayerDamageInformation, Skills.GetPlayers, JsonExporter.JsonSave,
    //       NotifyProcessor, Abnormality.RegisterBuff/ApplyBuff) runs on it. ExcelExporter,
    //       which does run on the upload thread, never touches it.
    //
    //   (2) the upload thread: the `new Thread(...)` AutomatedExport starts, which runs
    //       DpsServer.CheckAndSendFightData for each target, plus ExcelSave and Anonymize.
    //
    // ROOT CAUSE OF THE 2026-09 CORRUPTION BUG (this file's design is the fix):
    // earlier versions called PacketProcessor.Instance.PlayerTracker.Get(...) from
    // ApplyForServer, i.e. from thread (2). PlayerTracker._playerById is a plain
    // Dictionary<Tuple<uint,uint>, Player> with no synchronization at all, and thread (1)
    // writes into it (PlayerTracker.Update -> _playerById.Add, which can resize) whenever a
    // player entity is registered or updated. Reading a Dictionary while another thread Adds
    // to it is undefined behaviour in .NET and can permanently corrupt its bucket chains.
    //
    // Why that surfaced as "the support's buffs and debuffs vanish from the meter starting
    // at the 2nd boss": AbnormalityStorage.PlayerAbnormalityTime is keyed by the *Player
    // object reference*, and every buff begin/end goes through
    // Abnormality.RegisterBuff/ApplyBuff -> PlayerTracker.GetOrUpdate(user). Once the
    // dictionary is corrupted, TryGetValue misses a key that really is there, GetOrUpdate
    // takes its "not found" branch and allocates a SECOND Player instance for the same
    // character. From then on buffs are recorded under the new instance while the UI (which
    // resolves players through Database.PlayerDamageInformation -> PlayerTracker.Get) still
    // looks them up under the old one, so Get(oldPlayer) comes back empty. Worse, the
    // durations still open on the old instance never receive End(), so they keep
    // End == long.MaxValue, and AbnormalityDuration.Duration(begin,end) clamps that to the
    // end of the fight: exactly the reported "everything reads 100% uptime and the short
    // buffs disappear" signature. It starts at boss 2 because the upload thread only exists
    // once a boss has died, and boss 1's own stats were already snapshotted by GenerateStats
    // before that thread was even started.
    //
    // THE RULE, therefore:
    //   * Enrich() runs ONLY on thread (1). It is injected into AutomatedExport at the same
    //     point, on the same thread, doing the same work as stock JsonExporter.JsonSave,
    //     which iterates the same members, calls the same PlayerTracker lookup and builds
    //     the same JsonSkill list (and then does it a second time for received hits). So
    //     this is not "extra" work in a dangerous place: it is work the meter itself already
    //     performs right there whenever JSON export is enabled.
    //   * ApplyForServer() runs on thread (2) and may touch NOTHING except: the DpsServer's
    //     own UploadUrl (immutable config), the EncounterBase that thread already owns and
    //     mutates itself via stock Anonymize(), and the per-member backup list this file
    //     produced on thread (1). It must never call into PlayerTracker, Skills,
    //     AbnormalityStorage, EntityTracker or PacketProcessor.Instance. Ever.
    //
    // No LINQ / no lambdas / no nested types on purpose: this type gets merged into
    // DamageMeter.dll by IL rewriting, and compiler-generated closures or display classes
    // would be separate TypeDefs that the merge does not move.
    public static class RotationEnricher
    {
        // Only this host receives the heavy hit-by-hit dealtSkillLog. Every other upload
        // target gets it stripped, because they reject the oversized payload.
        private const string KeepHost = "enragedon";

        // HitDirection is a [Flags] enum, so Enum.ToString() on it is a reflection-driven
        // flag decomposition that allocates a fresh string on every single call: once per
        // hit, thousands of times per fight. Cache it by raw value instead. The text stays
        // byte-identical to what the meter's own SkillDealt tab shows (same enum, same
        // ToString), which matters because the site's backend maps exactly "Front"/"Side"/
        // "Back" and treats anything else as unknown. Only ever touched from thread (1).
        private static readonly string[] _dirNames = new string[128];

        // ---------------------------------------------------------------------------
        // PACKET THREAD ONLY. Injected into DataExporter.AutomatedExport(NpcEntity,
        // AbnormalityStorage) right after the "stats == null" guard.
        //
        // Builds each member's chronological dealt-hit timeline and parks it in the
        // internal Members.rotBackup field (added by pass1; internal, so Json.NET's default
        // contract resolver, which takes public fields and properties only, never
        // serializes it). dealtSkillLog itself is left null here, so the default for every
        // upload target is "no heavy payload", and ApplyForServer hands it to enragedon only.
        // ---------------------------------------------------------------------------
        internal static void Enrich(ExtendedStats stats)
        {
            try
            {
                if (stats == null || stats.BaseStats == null || stats.AllSkills == null) return;
                List<Members> members = stats.BaseStats.members;
                if (members == null) return;

                var skillDb = BasicTeraData.Instance.SkillDatabase;
                long firstTick = stats.FirstTick;

                for (int mi = 0; mi < members.Count; mi++)
                {
                    Members member = members[mi];
                    // Default state for every member: nothing to upload. Set before the
                    // per-member try so a failure below can never leave a stale list from a
                    // previous encounter attached to this one.
                    member.dealtSkillLog = null;
                    member.rotBackup = null;

                    try
                    {
                        // GetOrNull, never Get: Get is a raw Dictionary indexer and throws
                        // KeyNotFoundException for a player that is not tracked, which is
                        // exactly what happens on the anonymous upload pass where stock
                        // Anonymize() has already zeroed playerId.
                        Player player = PacketProcessor.Instance.PlayerTracker.GetOrNull(
                            member.playerServerId, member.playerId);
                        if (player == null) continue;

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

                        var log = new List<JsonSkill>(list.Count);
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
                                target = (sk.Target.Id.Id == ulong.MaxValue) ? null : sk.Target.Id.Id.ToString(),
                                dir = DirName(sk.Direction),
                                tgtName = ResolveTargetName(sk.Target)
                            };
                            log.Add(js);
                        }
                        member.rotBackup = log;
                    }
                    catch (Exception exMember)
                    {
                        SafeLog("Enrich member failed (serverId=" + member.playerServerId +
                                " playerId=" + member.playerId + "): " + exMember);
                    }
                }
            }
            catch (Exception ex)
            {
                SafeLog("Enrich failed: " + ex);
            }
        }

        // ---------------------------------------------------------------------------
        // UPLOAD THREAD. Injected at the start of DpsServer.CheckAndSendFightData, so it
        // runs once per upload target, before serialization.
        //
        // Touches only: server.UploadUrl, the EncounterBase this thread already owns, and
        // the per-member list Enrich parked on thread (1). Deliberately symmetric and
        // idempotent (it always assigns dealtSkillLog outright instead of clearing it
        // destructively), so target order, a target that bails out early, and two boss kills
        // whose uploads overlap can none of them make one target eat another's state.
        // ---------------------------------------------------------------------------
        internal static void ApplyForServer(DpsServer server, EncounterBase data)
        {
            try
            {
                if (data == null) return;
                List<Members> members = data.members;
                if (members == null) return;

                bool keep = false;
                if (server != null)
                {
                    Uri url = server.UploadUrl;
                    if (url != null && url.ToString().IndexOf(KeepHost, StringComparison.OrdinalIgnoreCase) >= 0)
                        keep = true;
                }

                for (int i = 0; i < members.Count; i++)
                {
                    Members m = members[i];
                    m.dealtSkillLog = keep ? m.rotBackup : null;
                }
            }
            catch { /* never break the upload */ }
        }

        // Same resolution the live SkillLog tab already uses (SkillLog.xaml.cs Update()):
        // a monster target shows its NpcInfo name, a player target (PvP) shows the account
        // name. Anything else (no target, e.g. a self-buff) gives null, never throws.
        // Both NpcEntity.Info and UserEntity.Name are plain auto-properties: pure reads,
        // no lookup, no side effect.
        static string ResolveTargetName(Entity ent)
        {
            if (ent == null) return null;
            var npc = ent as NpcEntity;
            if (npc != null) return npc.Info != null ? npc.Info.Name : null;
            var ue = ent as UserEntity;
            if (ue != null) return ue.Name;
            return null;
        }

        static string DirName(HitDirection d)
        {
            int raw = (int)d;
            if (raw < 0 || raw >= 128) return d.ToString();
            string s = _dirNames[raw];
            if (s == null)
            {
                s = d.ToString();
                _dirNames[raw] = s;
            }
            return s;
        }

        // Errors only. This lands in the meter's error.log, which players do read when
        // something breaks; logging every successful boss kill there would just be noise.
        static void SafeLog(string msg)
        {
            try { BasicTeraData.LogError("[ShinraRotationPatch] " + msg); }
            catch { /* never let logging break the upload */ }
        }
    }
}
