using System;
using System.Reflection;

namespace TAC_AI.AI.Forms.Smart.Learning
{
    /// <summary>
    /// P7 Item 20 (REV: P11 T5 Item 55 post-decompile rewrite). Per-player ID resolution
    /// using verified-engine APIs. The pre-P11 4-guess reflection on
    /// <c>m_SteamID</c>/<c>SteamId</c>/<c>steamID</c>/<c>SteamID</c> (none verified to
    /// exist on NetPlayer) is gone.
    ///
    /// Decompile evidence backing the resolution chain below:
    ///   - Tier 1 (EOS account-stable, cross-session): <c>ManEOS.inst.PlayerIDString</c>.
    ///     Verified at <c>F:\Tac-AI\Decompiled\AssemblyCSharp\ManEOS.cs:193</c> —
    ///     <c>public string PlayerIDString { get; private set; }</c>. ManEOS is the platform-
    ///     abstraction TerraTech uses for both Steam (account-id is included in the EOS auth
    ///     ticket per <c>ManEOS.cs:1018</c>) and Epic Games Store users.
    ///   - Tier 2 (Steam install-stable via reflection): the Steamworks.NET types are
    ///     bundled into Assembly-CSharp.dll's IL but not surfaced as a compile-time
    ///     namespace from our csproj reference. Resolved via
    ///     <c>Type.GetType("Steamworks.SteamUser, Assembly-CSharp")</c> +
    ///     <c>GetSteamID()</c> + <c>m_SteamID</c> field unpack — only fires when EOS
    ///     resolution didn't, so the overhead is one-time on first call.
    ///   - Tier 3 (session-stable Mirror handle): <c>ManNetwork.inst.MyPlayer.PlayerID</c>
    ///     (int) per <c>GUINPTInteraction.cs:595</c> + <c>AIGlobals.cs:128-143</c>.
    ///   - Tier 4 (hard fallback): <see cref="LearningService.DefaultPlayerId"/>.
    ///
    /// Each tier is exception-isolated; the first success wins.
    /// </summary>
    public static class PlayerIdResolver
    {
        // Steamworks reflection cache. Populated lazily on first use.
        private static bool _steamReflectionProbed;
        private static MethodInfo _steamGetSteamID;
        private static FieldInfo _csteamId_m_SteamID;
        private static readonly object _steamProbeGate = new object();

        public static string Resolve()
        {
            // Tier 1: EOS — verified string, no reflection.
            try
            {
                var eos = Singleton.Manager<ManEOS>.inst;
                if (eos != null && !string.IsNullOrEmpty(eos.PlayerIDString))
                    return "e" + eos.PlayerIDString;
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("playerid-eos-err",
                    "PlayerIdResolver Tier1 (EOS) threw " + ex.GetType().Name + ": " + ex.Message);
            }

            // Tier 2: Steam via reflection (types are in Assembly-CSharp.dll but not surfaced
            // as a compile-time namespace from our csproj reference).
            try
            {
                string steamId;
                if (TryGetSteamId(out steamId)) return "s" + steamId;
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("playerid-steam-err",
                    "PlayerIdResolver Tier2 (Steam) threw " + ex.GetType().Name + ": " + ex.Message);
            }

            // Tier 3: session-stable Mirror handle.
            try
            {
                var net = ManNetwork.inst;
                if (net != null && net.MyPlayer != null)
                {
                    int pid = net.MyPlayer.PlayerID;
                    if (pid != 0 && pid != -1) return "p" + pid;
                }
            }
            catch (Exception ex)
            {
                DebugTAC_AI.LogWarnFileOnly("playerid-mirror-err",
                    "PlayerIdResolver Tier3 (Mirror) threw " + ex.GetType().Name + ": " + ex.Message);
            }

            return LearningService.DefaultPlayerId;
        }

        /// <summary>
        /// Reflection-based Steamworks.SteamUser.GetSteamID() invoker. Returns the
        /// install-stable Steam ID as a decimal string. Falsy result (false / empty) when
        /// Steamworks isn't loaded, isn't initialized, or returns ID=0.
        /// </summary>
        public static bool TryGetSteamId(out string steamId)
        {
            steamId = null;
            EnsureSteamReflectionProbed();
            if (_steamGetSteamID == null || _csteamId_m_SteamID == null) return false;
            try
            {
                // SteamUser.GetSteamID() returns CSteamID (struct). Box once, unpack m_SteamID
                // (ulong) field — same pattern ManSteamworks.cs:156 uses internally.
                object cSteamId = _steamGetSteamID.Invoke(null, null);
                if (cSteamId == null) return false;
                object idObj = _csteamId_m_SteamID.GetValue(cSteamId);
                ulong id = Convert.ToUInt64(idObj);
                if (id == 0UL) return false;
                steamId = id.ToString();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureSteamReflectionProbed()
        {
            if (_steamReflectionProbed) return;
            lock (_steamProbeGate)
            {
                if (_steamReflectionProbed) return;
                try
                {
                    // The Steamworks.NET types are bundled into Assembly-CSharp.dll IL but
                    // not exposed as a referenceable namespace from our csproj. Probe via
                    // assembly-qualified Type.GetType.
                    var steamUserType = Type.GetType("Steamworks.SteamUser, Assembly-CSharp");
                    var cSteamIdType = Type.GetType("Steamworks.CSteamID, Assembly-CSharp");
                    if (steamUserType != null)
                        _steamGetSteamID = steamUserType.GetMethod("GetSteamID",
                            BindingFlags.Public | BindingFlags.Static);
                    if (cSteamIdType != null)
                        _csteamId_m_SteamID = cSteamIdType.GetField("m_SteamID",
                            BindingFlags.Public | BindingFlags.Instance);
                }
                catch { /* leave nulls — Tier 2 silently degrades */ }
                _steamReflectionProbed = true;
            }
        }
    }
}
