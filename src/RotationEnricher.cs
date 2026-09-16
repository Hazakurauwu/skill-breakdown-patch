using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using DamageMeter;
using DamageMeter.TeraDpsApi;
using Data;
using Tera.Game;
using Tera.Game.Messages;

// Compiled against the pass1 DamageMeter (which grants IVT to this assembly so we can see
// internal ExtendedStats, PacketProcessor internals and the internal Members.rotBackup field
// pass1 adds). After the build, this whole type is MERGED into DamageMeter.dll.
//
// No LINQ, no lambdas, no nested types, no string interpolation, no [ThreadStatic]: this type
// is merged by IL rewriting, and compiler-generated closures / display classes / handler
// structs would be separate TypeDefs or attribute refs that the merge does not move.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DamageMeter")]

namespace ShinraRotationPatch
{
    // =================================================================================
    // THREAD CONTRACT
    // =================================================================================
    //   (1) packet-processing thread: S_DESPAWN_NPC -> DataExporter.AutomatedExport, and every
    //       PacketProcessingFactory.Process call. The ONLY thread stock code uses to touch
    //       PlayerTracker / AbnormalityStorage / EntityTracker.
    //   (2) upload thread: the `new Thread(...)` AutomatedExport starts.
    //   Enrich + OnPacket run on (1). ApplyForServer runs on (2) and touches nothing but the
    //   DpsServer url, the EncounterBase that thread owns, and Members.rotBackup.
    public static class RotationEnricher
    {
        private const string KeepHost = "enragedon";

        // Per-hit attack direction and target name (v1.5 feature). These were briefly turned
        // off on 2026-09-16 while chasing a "party buffs vanish from the 2nd boss" report, on
        // the wrong suspicion that they caused it. They did not: the real cause is the meter's
        // own "Export packets logs" option (see the note on the patcher's PacketsExporter step).
        private const bool CaptureHitDetails = true;

        private const bool EnrichEnabled = true;

        // Full-session diagnostic trace (enragedon-diag-*.log next to DamageMeter.dll).
        // Investigation builds only. When false the patcher does not even inject OnPacket.
        private const bool FullTrace = false;

        private static readonly string[] _dirNames = new string[128];

        // ===========================================================================
        // PATCH: Enrich (packet thread) / ApplyForServer (upload thread)
        // ===========================================================================
        internal static void Enrich(ExtendedStats stats)
        {
            long t0 = Environment.TickCount64;
            if (FullTrace) TraceBossDeath(stats, "BEFORE", -1);
            if (!EnrichEnabled) return;
            int hits = 0;
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
                    member.dealtSkillLog = null;
                    member.rotBackup = null;
                    try
                    {
                        Player player = PacketProcessor.Instance.PlayerTracker.GetOrNull(member.playerServerId, member.playerId);
                        if (player == null) continue;

                        var list = new List<DamageMeter.Database.Structures.Skill>();
                        foreach (var s in stats.AllSkills.GetSkillsDealt(player.User, null, true))
                            list.Add(s);

                        for (int a = 1; a < list.Count; a++)
                        {
                            DamageMeter.Database.Structures.Skill key = list[a];
                            int b = a - 1;
                            while (b >= 0 && list[b].Time > key.Time) { list[b + 1] = list[b]; b--; }
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
                                dir = CaptureHitDetails ? DirName(sk.Direction) : null,
                                tgtName = CaptureHitDetails ? ResolveTargetName(sk.Target) : null
                            };
                            log.Add(js);
                        }
                        hits += log.Count;
                        member.rotBackup = log;
                    }
                    catch (Exception exMember)
                    {
                        LogErr("ENRICH_MEMBER_FAILED " + member.playerName + ": " + exMember);
                    }
                }
            }
            catch (Exception ex)
            {
                LogErr("ENRICH_FAILED: " + ex);
            }
            finally
            {
                if (FullTrace) TraceBossDeath(stats, "AFTER hits=" + hits, Environment.TickCount64 - t0);
            }
        }

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
            catch { }
        }

        // Errors always land somewhere: the trace file in investigation builds, otherwise the
        // meter's own error.log (which players do read when something breaks).
        static void LogErr(string msg)
        {
            if (FullTrace) { T(msg); return; }
            try { BasicTeraData.LogError("[ShinraRotationPatch] " + msg); } catch { }
        }

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
            if (s == null) { s = d.ToString(); _dirNames[raw] = s; }
            return s;
        }

        // ===========================================================================
        // FULL-SESSION TRACE
        // Writes enragedon-diag-<date>.log next to DamageMeter.dll. One RR is enough:
        //   * every packet counted by type; every abnormality begin/end/refresh classified by
        //     target (self / party / npc / other player / UNKNOWN ENTITY = silently dropped)
        //   * every user spawn/despawn, party list/leave/ban, zone change, login, spawn-me
        //   * a BEAT every 15s: per party member entity/out-of-range/player/live buff count,
        //     queue depth, paused, connected, overloaded, top packet types, exception counts
        //   * every exception in the whole process, INCLUDING ones swallowed by catch {},
        //     first occurrence with a stack, then counted
        //   * mirror socket connect / disconnect / warnings, process exit, unhandled crash
        //   * boss death before/after Enrich with party snapshot and Enrich time
        // ===========================================================================
        private static readonly object _tsync = new object();
        private static StreamWriter _tw;
        private static bool _tinit;
        private static bool _tdead;
        private static long _tLastBeat;
        private const long BeatMs = 15000;
        private static int _lineBudget = 400;

        private static readonly Dictionary<string, int> _pkt = new Dictionary<string, int>();
        private static long _pktWindow, _pktTotal;
        private static int _queueMax;
        // [0]=begin [1]=end [2]=refresh
        private static readonly int[] _abSelf = new int[3];
        private static readonly int[] _abParty = new int[3];
        private static readonly int[] _abNpc = new int[3];
        private static readonly int[] _abOtherUser = new int[3];
        private static readonly int[] _abUnknown = new int[3];
        private static readonly int[] _abPartyNoPlayer = new int[3];
        private static bool _lastPaused, _lastConnected, _lastOverloaded, _stateSeen;

        private static readonly Dictionary<string, int> _ex = new Dictionary<string, int>();
        private static int _exTotal;

        static void T(string line)
        {
            if (!FullTrace) return;
            if (Monitor.IsEntered(_tsync)) return; // re-entrancy from our own exception handler
            lock (_tsync)
            {
                try
                {
                    if (_tdead) return;
                    if (_tw == null) OpenTrace();
                    if (_tw == null) return;
                    _tw.Write(DateTime.Now.ToString("HH:mm:ss.fff"));
                    _tw.Write(" [t");
                    _tw.Write(Thread.CurrentThread.ManagedThreadId);
                    _tw.Write("] ");
                    _tw.WriteLine(line);
                }
                catch { _tdead = true; }
            }
        }

        static void Flush()
        {
            if (Monitor.IsEntered(_tsync)) return;
            lock (_tsync) { try { if (_tw != null) _tw.Flush(); } catch { } }
        }

        static void OpenTrace()
        {
            // caller holds _tsync
            try
            {
                string dir = Path.GetDirectoryName(typeof(RotationEnricher).Assembly.Location);
                string path = Path.Combine(dir, "enragedon-diag-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                _tw = new StreamWriter(fs, new UTF8Encoding(false));
                try { BasicTeraData.LogError("[ShinraRotationPatch] full diagnostic trace -> " + path); } catch { }
            }
            catch { _tdead = true; _tw = null; }
        }

        static void TraceInit()
        {
            _tinit = true;
            _tLastBeat = Environment.TickCount64;
            try
            {
                T("===== ENRAGEDON DIAG TRACE START =====");
                string dll = typeof(RotationEnricher).Assembly.Location;
                T("dll=" + dll + " size=" + new FileInfo(dll).Length + " md5=" + Md5(dll));
                T("flags EnrichEnabled=" + EnrichEnabled + " CaptureHitDetails=" + CaptureHitDetails);
                try
                {
                    var wd = BasicTeraData.Instance.WindowData;
                    T("config capture_mode=" + wd.CaptureMode + " packets_collect=" + wd.PacketsCollect + " enable_chat=" + wd.EnableChat);
                }
                catch (Exception e) { T("config read failed: " + e.Message); }
                try
                {
                    var servers = DataExporter.DpsServers;
                    for (int i = 0; i < servers.Count; i++)
                    {
                        var s = servers[i];
                        string host = (s.UploadUrl != null) ? s.UploadUrl.Host : "(null)";
                        T("dps_server host=" + host + " enabled=" + s.Enabled + " anonymous=" + s.AnonymousUpload);
                    }
                }
                catch (Exception e) { T("dps servers read failed: " + e.Message); }

                AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
                AppDomain.CurrentDomain.ProcessExit += OnExit;
                try
                {
                    var sn = PacketProcessor.Instance.Sniffer;
                    sn.NewConnection += OnSnifferNew;
                    sn.EndConnection += OnSnifferEnd;
                    sn.Warning += OnSnifferWarning;
                    T("sniffer type=" + sn.GetType().FullName + " connected=" + sn.Connected);
                }
                catch (Exception e) { T("sniffer hook failed: " + e.Message); }
            }
            catch (Exception e) { T("TRACE INIT FAILED: " + e); }
            Flush();
        }

        static string Md5(string path)
        {
            try
            {
                using (var md5 = System.Security.Cryptography.MD5.Create())
                using (var f = File.OpenRead(path))
                {
                    byte[] h = md5.ComputeHash(f);
                    var sb = new StringBuilder();
                    for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return "?"; }
        }

        // Injected at the start of PacketProcessingFactory.Process(ParsedMessage). Packet thread.
        internal static void OnPacket(ParsedMessage m)
        {
            if (!FullTrace || m == null) return;
            try
            {
                if (!_tinit) TraceInit();
                var pp = PacketProcessor.Instance;

                _pktWindow++; _pktTotal++;
                string tn = m.GetType().Name;
                int c;
                _pkt.TryGetValue(tn, out c);
                _pkt[tn] = c + 1;

                try { int q = pp.Sniffer.Packets.Count; if (q > _queueMax) _queueMax = q; } catch { }

                var ab = m as SAbnormalityBegin;
                if (ab != null) ClassifyAbn(pp, ab.TargetId, 0, ab.AbnormalityId);
                else
                {
                    var ae = m as SAbnormalityEnd;
                    if (ae != null) ClassifyAbn(pp, ae.TargetId, 1, ae.AbnormalityId);
                    else
                    {
                        var ar = m as SAbnormalityRefresh;
                        if (ar != null) ClassifyAbn(pp, ar.TargetId, 2, ar.AbnormalityId);
                        else TraceEvent(pp, m);
                    }
                }

                try
                {
                    bool paused = pp.PacketProcessing.Paused;
                    bool conn = pp.Sniffer.Connected;
                    bool over = pp.Overloaded;
                    if (!_stateSeen || paused != _lastPaused || conn != _lastConnected || over != _lastOverloaded)
                    {
                        T("STATE paused=" + paused + " connected=" + conn + " overloaded=" + over);
                        _lastPaused = paused; _lastConnected = conn; _lastOverloaded = over; _stateSeen = true;
                    }
                }
                catch { }

                long now = Environment.TickCount64;
                if (now - _tLastBeat >= BeatMs) Beat(pp, now);
            }
            catch { }
        }

        static void ClassifyAbn(PacketProcessor pp, EntityId target, int kind, int abnId)
        {
            try
            {
                var et = pp.EntityTracker;
                Entity e = (et != null) ? et.GetOrNull(target) : null;
                if (e == null)
                {
                    _abUnknown[kind]++;
                    if (_abUnknown[kind] <= 20 && _lineBudget > 0) { _lineBudget--; T("ABN_TARGET_UNKNOWN kind=" + kind + " abn=" + abnId + " target=" + target.Id); }
                    return;
                }
                var ue = e as UserEntity;
                if (ue == null) { _abNpc[kind]++; return; }
                UserEntity me = et.MeterUser;
                if (me != null && ue.Id == me.Id) { _abSelf[kind]++; return; }
                bool party = pp.PlayerTracker.MyParty(ue.ServerId, ue.PlayerId);
                if (!party) { _abOtherUser[kind]++; return; }
                _abParty[kind]++;
                Player p = pp.PlayerTracker.GetOrNull(ue.ServerId, ue.PlayerId);
                if (p == null)
                {
                    _abPartyNoPlayer[kind]++;
                    if (_abPartyNoPlayer[kind] <= 20 && _lineBudget > 0) { _lineBudget--; T("ABN_PARTY_NO_PLAYER kind=" + kind + " abn=" + abnId + " name=" + ue.Name); }
                }
            }
            catch { }
        }

        static void TraceEvent(PacketProcessor pp, ParsedMessage m)
        {
            var su = m as SpawnUserServerMessage;
            if (su != null)
            {
                bool party = false;
                try { party = pp.PlayerTracker.MyParty(su.ServerId, su.PlayerId); } catch { }
                if (party || _lineBudget > 0)
                {
                    if (!party) _lineBudget--;
                    T("SPAWN_USER " + su.Name + " id=" + su.Id.Id + " srv=" + su.ServerId + " pid=" + su.PlayerId + " party=" + party + " dead=" + su.Dead);
                }
                return;
            }
            var du = m as SDespawnUser;
            if (du != null)
            {
                string name = "?"; bool party = false; bool known = false;
                try
                {
                    var ue = pp.EntityTracker.GetOrNull(du.User) as UserEntity;
                    if (ue != null) { known = true; name = ue.Name; party = pp.PlayerTracker.MyParty(ue.ServerId, ue.PlayerId); }
                }
                catch { }
                if (party || _lineBudget > 0)
                {
                    if (!party) _lineBudget--;
                    T("DESPAWN_USER " + name + " id=" + du.User.Id + " known=" + known + " party=" + party);
                }
                return;
            }
            var pl = m as S_PARTY_MEMBER_LIST;
            if (pl != null)
            {
                var sb = new StringBuilder("PARTY_LIST count=" + pl.Party.Count + " :");
                for (int i = 0; i < pl.Party.Count; i++)
                {
                    PartyMember pm = pl.Party[i];
                    sb.Append(" ").Append(pm.Name).Append("(").Append(pm.PlayerClass.ToString()).Append(" srv=").Append(pm.ServerId)
                      .Append(" pid=").Append(pm.PlayerId).Append(" id=").Append(pm.Id.Id).Append(")");
                }
                T(sb.ToString());
                Flush();
                return;
            }
            var lm = m as S_LEAVE_PARTY_MEMBER;
            if (lm != null) { T("LEAVE_PARTY_MEMBER " + lm.Name); Flush(); return; }
            var bm = m as S_BAN_PARTY_MEMBER;
            if (bm != null) { T("BAN_PARTY_MEMBER " + bm.Name); Flush(); return; }
            if (m is S_LEAVE_PARTY) { T("LEAVE_PARTY"); Flush(); return; }
            var topo = m as S_LOAD_TOPO;
            if (topo != null) { T("LOAD_TOPO area=" + topo.AreaId + " (meter clears its packet queue here)"); Flush(); return; }
            var login = m as LoginServerMessage;
            if (login != null) { T("LOGIN " + login.Name + " srv=" + login.ServerId + " pid=" + login.PlayerId); Flush(); return; }
            var sme = m as SpawnMeServerMessage;
            if (sme != null) { T("SPAWN_ME id=" + sme.Id.Id + " dead=" + sme.Dead); return; }
        }

        static void Beat(PacketProcessor pp, long now)
        {
            _tLastBeat = now;
            _lineBudget = 400;
            try
            {
                string meter = "?";
                try { if (pp.EntityTracker != null && pp.EntityTracker.MeterUser != null) meter = pp.EntityTracker.MeterUser.Name; } catch { }
                T("BEAT pkts15s=" + _pktWindow + " pktsTotal=" + _pktTotal + " queueMax=" + _queueMax + " meter=" + meter);
                T("  abn[begin/end/refresh] self=" + J(_abSelf) + " party=" + J(_abParty) + " npc=" + J(_abNpc)
                  + " otherUser=" + J(_abOtherUser) + " UNKNOWN_TARGET=" + J(_abUnknown) + " partyNoPlayer=" + J(_abPartyNoPlayer));
                TraceParty(pp, "  ");
                T("  top packets: " + TopPackets(12));
                T("  exceptions so far: " + ExSummary(12));
            }
            catch (Exception e) { T("BEAT failed: " + e.Message); }
            _pktWindow = 0; _queueMax = 0; _pkt.Clear();
            for (int i = 0; i < 3; i++)
            {
                _abSelf[i] = 0; _abParty[i] = 0; _abNpc[i] = 0;
                _abOtherUser[i] = 0; _abUnknown[i] = 0; _abPartyNoPlayer[i] = 0;
            }
            Flush();
        }

        static void TraceParty(PacketProcessor pp, string indent)
        {
            try
            {
                var pt = pp.PlayerTracker;
                var et = pp.EntityTracker;
                var live = pp.AbnormalityStorage;
                List<UserEntity> party = pt.PartyList();
                var sb = new StringBuilder(indent + "party size=" + pt.PartySize + " listed=" + party.Count + " :");
                for (int i = 0; i < party.Count; i++)
                {
                    UserEntity ue = party[i];
                    int ent = 0, oor = 0, res = 0, nLive = -1;
                    try { ent = (et.GetOrNull(ue.Id) != null) ? 1 : 0; } catch { }
                    try { oor = ue.OutOfRange ? 1 : 0; } catch { }
                    try
                    {
                        Player p = pt.GetOrNull(ue.ServerId, ue.PlayerId);
                        if (p != null) { res = 1; nLive = live.Get(p).Times.Count; }
                    }
                    catch { }
                    sb.Append(" | ").Append(ue.Name).Append(" ent=").Append(ent).Append(" oor=").Append(oor)
                      .Append(" res=").Append(res).Append(" buffs=").Append(nLive);
                }
                T(sb.ToString());
            }
            catch (Exception e) { T(indent + "party snapshot failed: " + e.Message); }
        }

        static void TraceBossDeath(ExtendedStats stats, string phase, long ms)
        {
            try
            {
                if (!_tinit) TraceInit();
                var pp = PacketProcessor.Instance;
                string boss = "?";
                try { boss = stats.BaseStats.areaId + "/" + stats.BaseStats.bossId; } catch { }
                int q = -1;
                try { q = pp.Sniffer.Packets.Count; } catch { }
                int bossDeb = -1;
                try { if (stats.Entity != null) bossDeb = pp.AbnormalityStorage.Get(stats.Entity).Count; } catch { }
                T("BOSS_DEATH " + phase + " boss=" + boss + " enrichMs=" + ms + " queueNow=" + q + " liveBossDebuffs=" + bossDeb);
                if (ms < 0) TraceParty(pp, "  ");
                Flush();
            }
            catch { }
        }

        static string J(int[] a) { return a[0] + "/" + a[1] + "/" + a[2]; }

        static string TopPackets(int n)
        {
            var keys = new List<string>(_pkt.Keys);
            var sb = new StringBuilder();
            for (int k = 0; k < n && keys.Count > 0; k++)
            {
                int bi = 0; int bv = -1;
                for (int i = 0; i < keys.Count; i++) { int v = _pkt[keys[i]]; if (v > bv) { bv = v; bi = i; } }
                sb.Append(keys[bi]).Append('=').Append(bv).Append(' ');
                keys.RemoveAt(bi);
            }
            return sb.ToString();
        }

        static string ExSummary(int n)
        {
            if (Monitor.IsEntered(_tsync)) return "(busy)";
            lock (_tsync)
            {
                var keys = new List<string>(_ex.Keys);
                var sb = new StringBuilder("total=" + _exTotal + " ");
                for (int k = 0; k < n && keys.Count > 0; k++)
                {
                    int bi = 0; int bv = -1;
                    for (int i = 0; i < keys.Count; i++) { int v = _ex[keys[i]]; if (v > bv) { bv = v; bi = i; } }
                    sb.Append("[").Append(bv).Append("x ").Append(keys[bi]).Append("] ");
                    keys.RemoveAt(bi);
                }
                return sb.ToString();
            }
        }

        static void OnFirstChance(object sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (Monitor.IsEntered(_tsync)) return;
            string firstLine = null;
            lock (_tsync)
            {
                try
                {
                    Exception ex = e.Exception;
                    string msg = ex.Message ?? "";
                    if (msg.Length > 160) msg = msg.Substring(0, 160);
                    string key = ex.GetType().Name + ": " + msg.Replace('\r', ' ').Replace('\n', ' ');
                    int c;
                    bool first = !_ex.TryGetValue(key, out c);
                    _ex[key] = c + 1;
                    _exTotal++;
                    if (first)
                    {
                        var st = new System.Diagnostics.StackTrace(1, false);
                        var sb = new StringBuilder("FIRST_EXCEPTION " + key + " @ ");
                        int frames = st.FrameCount < 8 ? st.FrameCount : 8;
                        for (int i = 0; i < frames; i++)
                        {
                            var mb = st.GetFrame(i).GetMethod();
                            if (mb == null) continue;
                            sb.Append(mb.DeclaringType != null ? mb.DeclaringType.FullName : "?").Append('.').Append(mb.Name).Append(" < ");
                        }
                        firstLine = sb.ToString();
                    }
                }
                catch { }
            }
            if (firstLine != null) { T(firstLine); Flush(); }
        }

        static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            T("UNHANDLED_EXCEPTION terminating=" + e.IsTerminating + " " + e.ExceptionObject);
            Flush();
        }

        static void OnExit(object sender, EventArgs e)
        {
            T("PROCESS_EXIT exceptions: " + ExSummary(30));
            Flush();
        }

        static void OnSnifferNew(Server s)
        {
            T("SNIFFER_NEW_CONNECTION server=" + (s != null ? s.Name : "?") + "  <-- if this appears mid-session, the mirror socket reconnected");
            Flush();
        }

        static void OnSnifferEnd()
        {
            T("SNIFFER_END_CONNECTION  <-- mirror socket dropped");
            Flush();
        }

        static void OnSnifferWarning(string w)
        {
            T("SNIFFER_WARNING " + w);
            Flush();
        }
    }
}
