using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using TAC_AI.AI;
using TAC_AI.AI.Enemy;
using TAC_AI.World;
using UnityEngine.Networking;

namespace TAC_AI
{
    /// REVISED (overview): the RTS/AI/retreat TryBroadcast helpers are now bi-directional. Each
    /// gates on !ManNetwork.IsNetworked (SP short-circuit) instead of HostExists, then branches on
    /// IsHost: host fans out via SendToAllExceptClient, client sends upstream via SendToServer. The
    /// matching OnServer* receive handlers now echo the payload to all clients except the sender
    /// (SendToAllExceptClient(netMsg.conn.connectionId,...)) so a client's order propagates host->others.
    /// AIEnemySet and AIEnemyStagedSiege gained their Serialize/Deserialize overrides (were missing),
    /// and the two host-only enemy/siege broadcasts now send on AIEnemyType / AIEnemySiege (were AIRetreatRequest).
    internal static class NetworkHandler
    {
        static NetworkInstanceId Host;
        static bool HostExists = false;

        const TTMsgType AIADVTypeChange = (TTMsgType)4317;
        const TTMsgType AIRetreatRequest = (TTMsgType)4318;
        const TTMsgType AIRTSPosCommand = (TTMsgType)4319;
        const TTMsgType AIRTSPosControl = (TTMsgType)4320;
        const TTMsgType AIRTSAttack = (TTMsgType)4321;
        const TTMsgType AIEnemyType = (TTMsgType)4322;
        const TTMsgType AIEnemySiege = (TTMsgType)4323;
        const TTMsgType AIProfileChange = (TTMsgType)4324;   // Step 7: allied composable-profile selection

        public class AIProfileChangeMessage : MessageBase
        {
            public AIProfileChangeMessage() { }
            public AIProfileChangeMessage(uint netTechID, string profileId)
            {
                this.netTechID = netTechID;
                this.profileId = profileId ?? "";
            }
            public override void Deserialize(NetworkReader reader)
            {
                netTechID = reader.ReadUInt32();
                profileId = reader.ReadString();
            }
            public override void Serialize(NetworkWriter writer)
            {
                writer.Write(netTechID);
                writer.Write(profileId ?? "");
            }
            public uint netTechID;
            public string profileId;
        }
        public class AITypeChangeMessage : MessageBase
        {
            public AITypeChangeMessage() { }
            public AITypeChangeMessage(uint netTechID, AIType AIType, AIDriverType AIDriving)
            {
                this.netTechID = netTechID;
                this.AIType = AIType;
                this.AIDriving = AIDriving;
            }
            public override void Deserialize(NetworkReader reader)
            {
                netTechID = reader.ReadUInt32();
                AIType = (AIType)reader.ReadInt32();
                AIDriving = (AIDriverType)reader.ReadInt32();
            }

            public override void Serialize(NetworkWriter writer)
            {
                writer.Write(netTechID);
                writer.Write((int)AIType);
                writer.Write((int)AIDriving);
            }

            public uint netTechID;
            public AIType AIType;
            public AIDriverType AIDriving;
        }
        public class AIRetreatMessage : MessageBase
        {
            public AIRetreatMessage() { }
            public AIRetreatMessage(int team, bool retreat)
            {
                Team = team;
                Retreat = retreat;
            }
            public override void Deserialize(NetworkReader reader)
            {
                Team = reader.ReadInt32();
                Retreat = reader.ReadBoolean();
            }

            public override void Serialize(NetworkWriter writer)
            {
                writer.Write(Team);
                writer.Write(Retreat);
            }

            public int Team;
            public bool Retreat;
        }

        public class AIRTSCommandMessage : MessageBase
        {
            public AIRTSCommandMessage() { }
            public AIRTSCommandMessage(uint netTechID, Vector3 PosIn)
            {
                this.netTechID = netTechID;
                this.Position = PosIn;
            }
            public override void Deserialize(NetworkReader reader)
            {
                netTechID = reader.ReadUInt32();
                Position = reader.ReadVector3();
            }

            public override void Serialize(NetworkWriter writer)
            {
                writer.Write(netTechID);
                writer.Write(Position);
            }

            public uint netTechID;
            public Vector3 Position = Vector3.zero;
        }
        public class AIRTSControlMessage : MessageBase
        {
            public AIRTSControlMessage() { }
            public AIRTSControlMessage(uint netTechID, bool isRTS)
            {
                this.netTechID = netTechID;
                this.RTSControl = isRTS;
            }
            public override void Deserialize(NetworkReader reader)
            {
                netTechID = reader.ReadUInt32();
                RTSControl = reader.ReadBoolean();
            }

            public override void Serialize(NetworkWriter writer)
            {
                writer.Write(netTechID);
                writer.Write(RTSControl);
            }

            public uint netTechID;
            public bool RTSControl = false;
        }
        public class AIRTSAttackComm : MessageBase
        {
            public AIRTSAttackComm() { }
            public AIRTSAttackComm(uint netTechID, uint netTechIDTarget)
            {
                this.netTechID = netTechID;
                this.targetNetTechID = netTechIDTarget;
            }
            public override void Deserialize(NetworkReader reader)
            {
                netTechID = reader.ReadUInt32();
                targetNetTechID = reader.ReadUInt32();
            }

            public override void Serialize(NetworkWriter writer)
            {
                writer.Write(netTechID);
                writer.Write(targetNetTechID);
            }

            public uint netTechID;
            public uint targetNetTechID;
        }

        public class AIEnemySet : MessageBase
        {
            public AIEnemySet() { }
            public AIEnemySet(uint netTechID, EnemySmarts EnemyType)
            {
                this.netTechID = netTechID;
                this.enemyType = (int)EnemyType;
            }
            // REVISED: wire serialization added (read/write order must mirror); was missing, so the message never crossed the wire
            public override void Deserialize(NetworkReader reader)
            {
                netTechID = reader.ReadUInt32();
                enemyType = reader.ReadInt32();
            }

            public override void Serialize(NetworkWriter writer)
            {
                writer.Write(netTechID);
                writer.Write(enemyType);
            }

            public uint netTechID;
            public int enemyType;
        }

        public class AIEnemyStagedSiege : MessageBase
        {
            public AIEnemyStagedSiege() { }
            public AIEnemyStagedSiege(int team, int teamTarget, long totalHP, bool start)
            {
                Team = team;
                TeamTargeted = teamTarget;
                MaxHP = totalHP;
                Starting = start;
            }
            // REVISED: wire serialization added (read/write order must mirror); was missing, so the message never crossed the wire
            public override void Deserialize(NetworkReader reader)
            {
                Team = reader.ReadInt32();
                TeamTargeted = reader.ReadInt32();
                MaxHP = reader.ReadInt64();
                Starting = reader.ReadBoolean();
            }

            public override void Serialize(NetworkWriter writer)
            {
                writer.Write(Team);
                writer.Write(TeamTargeted);
                writer.Write(MaxHP);
                writer.Write(Starting);
            }

            public int Team;
            public int TeamTargeted;
            public long MaxHP;
            public bool Starting;
        }

        private static int localConnectionID { get { return ManNetwork.inst.Client.connection.connectionId; } }


        // AIRTSCommandMessage
        // REVISED: now bi-directional (same shape across all TryBroadcast* below). Gates on
        // !ManNetwork.IsNetworked (was HostExists); host fans out to clients via SendToAllExceptClient,
        // client sends upstream to host via SendToServer.
        public static void TryBroadcastRTSCommand(uint netTechID, Vector3 Pos)
        {
            if (!ManNetwork.IsNetworked) return;
            try
            {
                var msg = new AIRTSCommandMessage(netTechID, Pos);
                if (ManNetwork.IsHost)
                {
                    DebugTAC_AI.LogNet("Sent RTSCommand fan-out to clients (host)");
                    Singleton.Manager<ManNetwork>.inst.SendToAllExceptClient(localConnectionID, AIRTSPosCommand, msg, Host);
                }
                else
                {
                    DebugTAC_AI.LogNet("Sent RTSCommand upstream to host (client)");
                    Singleton.Manager<ManNetwork>.inst.SendToServer(AIRTSPosCommand, msg);
                }
            }
            catch { DebugTAC_AI.LogNet(KickStart.ModID + ": Failed to send TryBroadcastRTSCommand!"); }
        }
        public static void OnClientAcceptRTSCommand(NetworkMessage netMsg)
        {
            var reader = new AIRTSCommandMessage();
            netMsg.ReadMessage(reader);
            try
            {
                NetTech find = ManNetTechs.inst.FindTech(reader.netTechID);
                find.tech.GetHelperInsured().DirectRTSDest(reader.Position);
                DebugTAC_AI.LogNet(KickStart.ModID + ": Received new OnClientAcceptRTSCommand update, ordering tech " + find.name + " to " + reader.Position);
            }
            catch
            {
                DebugTAC_AI.Assert(true, KickStart.ModID + ": OnClientAcceptRTSCommand  - Receive failiure! \n Our techs are now desynched!");
            }
        }
        public static void OnServerAcceptRTSCommand(NetworkMessage netMsg)
        {
            var reader = new AIRTSCommandMessage();
            netMsg.ReadMessage(reader);
            try
            {
                NetTech find = ManNetTechs.inst.FindTech(reader.netTechID);
                find.tech.GetHelperInsured().DirectRTSDest(reader.Position);
                DebugTAC_AI.LogNet(KickStart.ModID + ": Received new OnServerAcceptRTSCommand update, ordering tech " + find.name + " to " + reader.Position);
                // REVISED: server echo (same shape across all OnServer* handlers below) - host re-broadcasts the
                // client's payload to every client except the sender so the order propagates host->others
                Singleton.Manager<ManNetwork>.inst.SendToAllExceptClient(netMsg.conn.connectionId, AIRTSPosCommand, reader, Host);
            }
            catch
            {
                DebugTAC_AI.Assert(true, KickStart.ModID + ": OnServerAcceptRTSCommand  - Receive failiure! \n Our techs are now desynched!");
            }
        }

        // AIRTSControlMessage
        public static void TryBroadcastRTSControl(uint netTechID, bool isRTS)
        {
            if (!ManNetwork.IsNetworked) return;
            try
            {
                var msg = new AIRTSControlMessage(netTechID, isRTS);
                if (ManNetwork.IsHost)
                {
                    DebugTAC_AI.LogNet("Sent RTSControl fan-out to clients (host)");
                    Singleton.Manager<ManNetwork>.inst.SendToAllExceptClient(localConnectionID, AIRTSPosControl, msg, Host);
                }
                else
                {
                    DebugTAC_AI.LogNet("Sent RTSControl upstream to host (client)");
                    Singleton.Manager<ManNetwork>.inst.SendToServer(AIRTSPosControl, msg);
                }
            }
            catch { DebugTAC_AI.LogNet(KickStart.ModID + ": Failed to send TryBroadcastRTSControl!"); }
        }
        public static void OnClientAcceptRTSControl(NetworkMessage netMsg)
        {
            var reader = new AIRTSControlMessage();
            netMsg.ReadMessage(reader);
            try
            {
                NetTech find = ManNetTechs.inst.FindTech(reader.netTechID);
                find.tech.GetHelperInsured().isRTSControlled = reader.RTSControl;
                DebugTAC_AI.LogNet(KickStart.ModID + ": Received new OnClientAcceptRTSControl update,  Tech " + find.name + "'s RTS control is " + reader.RTSControl);
            }
            catch
            {
                DebugTAC_AI.Assert(true, KickStart.ModID + ": OnClientAcceptRTSControl  - Receive failiure! \n Our techs are now desynched!");
            }
        }
        public static void OnServerAcceptRTSControl(NetworkMessage netMsg)
        {
            var reader = new AIRTSControlMessage();
            netMsg.ReadMessage(reader);
            try
            {
                NetTech find = ManNetTechs.inst.FindTech(reader.netTechID);
                find.tech.GetHelperInsured().isRTSControlled = reader.RTSControl;
                DebugTAC_AI.LogNet(KickStart.ModID + ": Received new OnServerAcceptRTSControl update, Tech " + find.name + "'s RTS control is " + reader.RTSControl);
                Singleton.Manager<ManNetwork>.inst.SendToAllExceptClient(netMsg.conn.connectionId, AIRTSPosControl, reader, Host);
            }
            catch
            {
                DebugTAC_AI.Assert(true, KickStart.ModID + ": OnServerAcceptRTSControl  - Receive failiure! \n Our techs are now desynched!");

            }
        }

        // AIRTSAttackComm
        public static void TryBroadcastRTSAttack(uint netTechID, uint TargetNetTechID)
        {
            if (!ManNetwork.IsNetworked) return;
            try
            {
                var msg = new AIRTSAttackComm(netTechID, TargetNetTechID);
                if (ManNetwork.IsHost)
                {
                    DebugTAC_AI.LogNet("Sent RTSAttack fan-out to clients (host)");
                    Singleton.Manager<ManNetwork>.inst.SendToAllExceptClient(localConnectionID, AIRTSAttack, msg, Host);
                }
                else
                {
                    DebugTAC_AI.LogNet("Sent RTSAttack upstream to host (client)");
                    Singleton.Manager<ManNetwork>.inst.SendToServer(AIRTSAttack, msg);
                }
            }
            catch { DebugTAC_AI.LogNet(KickStart.ModID + ": Failed to send TryBroadcastRTSAttack!"); }
        }
        public static void OnClientAcceptRTSAttack(NetworkMessage netMsg)
        {
            var reader = new AIRTSAttackComm();
            netMsg.ReadMessage(reader);
            try
            {
                NetTech find = ManNetTechs.inst.FindTech(reader.netTechID);
                NetTech targeting = ManNetTechs.inst.FindTech(reader.targetNetTechID);
                var helper = find.tech.GetHelperInsured();
                helper.lastEnemy = targeting.tech.visible;
                DebugTAC_AI.LogNet(KickStart.ModID + ": Received new OnClientAcceptRTSAttack update,  tech " + find.name + "'s RTS target is " + targeting.tech.name);
            }
            catch
            {
                DebugTAC_AI.Assert(true, KickStart.ModID + ": OnClientAcceptRTSAttack  - Receive failiure! \n Our techs are now desynched!");
            }
        }
        public static void OnServerAcceptRTSAttack(NetworkMessage netMsg)
        {
            var reader = new AIRTSAttackComm();
            netMsg.ReadMessage(reader);
            try
            {
                NetTech find = ManNetTechs.inst.FindTech(reader.netTechID);
                NetTech targeting = ManNetTechs.inst.FindTech(reader.targetNetTechID);
                var helper = find.tech.GetHelperInsured();
                helper.lastEnemy = targeting.tech.visible;
                DebugTAC_AI.LogNet(KickStart.ModID + ": Received new OnServerAcceptRTSAttack update,  tech " + find.name + "'s RTS target is " + targeting.tech.name);
                Singleton.Manager<ManNetwork>.inst.SendToAllExceptClient(netMsg.conn.connectionId, AIRTSAttack, reader, Host);
            }
            catch
            {
                DebugTAC_AI.Assert(true, KickStart.ModID + ": OnServerAcceptRTSAttack  - Receive failiure! \n Our techs are now desynched!");
            }
        }

        // AITypeChangeMessage
        public static void TryBroadcastNewAIState(uint netTechID, AIType AIType, AIDriverType AIDriver)
        {
            if (!ManNetwork.IsNetworked) return;
            try
            {
                var msg = new AITypeChangeMessage(netTechID, AIType, AIDriver);
                if (ManNetwork.IsHost)
                {
                    DebugTAC_AI.LogNet("Sent AdvancedAI fan-out to clients (host)");
                    Singleton.Manager<ManNetwork>.inst.SendToAllExceptClient(localConnectionID, AIADVTypeChange, msg, Host);
                }
                else
                {
                    DebugTAC_AI.LogNet("Sent AdvancedAI upstream to host (client)");
                    Singleton.Manager<ManNetwork>.inst.SendToServer(AIADVTypeChange, msg);
                }
            }
            catch { DebugTAC_AI.LogNet(KickStart.ModID + ": Failed to send new AdvancedAI update, shouldn't be too bad in the long run"); }
        }
        public static void OnClientSetNewAIState(NetworkMessage netMsg)
        {
            var reader = new AITypeChangeMessage();
            netMsg.ReadMessage(reader);
            try
            {
                NetTech find = ManNetTechs.inst.FindTech(reader.netTechID);
                var helper = find.tech.GetHelperInsured();
                helper.TrySetAITypeRemote(netMsg.GetSender(), reader.AIType, reader.AIDriving);
                DebugTAC_AI.LogNet(KickStart.ModID + ": Received new OnClientSetNewAIState update, tech " + find.name + " changing to " + helper.DediAI.ToString()
                    + " | Driver: " + helper.DriverType.ToString());
            }
            catch
            {
                DebugTAC_AI.Assert(true, KickStart.ModID + ": OnClientSetNewAIState - Receive failiure! \n Our techs are now desynched!");
            }
        }
        public static void OnServerSetNewAIState(NetworkMessage netMsg)
        {
            var reader = new AITypeChangeMessage();
            netMsg.ReadMessage(reader);
            try
            {
                NetTech find = ManNetTechs.inst.FindTech(reader.netTechID);
                var helper = find.tech.GetHelperInsured();
                helper.TrySetAITypeRemote(netMsg.GetSender(), reader.AIType, reader.AIDriving);
                DebugTAC_AI.LogNet(KickStart.ModID + ": Received new OnServerSetNewAIState update, tech " + find.name + " changing to " + helper.DediAI.ToString()
                    + " | Driver: " + helper.DriverType.ToString());
                Singleton.Manager<ManNetwork>.inst.SendToAllExceptClient(netMsg.conn.connectionId, AIADVTypeChange, reader, Host);
            }
            catch
            {
                DebugTAC_AI.Assert(true, KickStart.ModID + ": OnServerSetNewAIState - Receive failiure! \n Our techs are now desynched!");
            }
        }

        // AIProfileChangeMessage (allied composable-profile selection) - mirrors AITypeChangeMessage flow.
        public static void TryBroadcastNewAIProfile(uint netTechID, string profileId)
        {
            if (!ManNetwork.IsNetworked) return;
            try
            {
                var msg = new AIProfileChangeMessage(netTechID, profileId);
                if (ManNetwork.IsHost)
                    Singleton.Manager<ManNetwork>.inst.SendToAllExceptClient(localConnectionID, AIProfileChange, msg, Host);
                else
                    Singleton.Manager<ManNetwork>.inst.SendToServer(AIProfileChange, msg);
            }
            catch { DebugTAC_AI.LogNet(KickStart.ModID + ": Failed to send AI profile update"); }
        }
        public static void OnClientSetNewAIProfile(NetworkMessage netMsg)
        {
            var reader = new AIProfileChangeMessage();
            netMsg.ReadMessage(reader);
            try
            {
                NetTech find = ManNetTechs.inst.FindTech(reader.netTechID);
                var helper = find.tech.GetHelperInsured();
                helper.TrySetAIProfileRemote(netMsg.GetSender(), reader.profileId);
            }
            catch { DebugTAC_AI.Assert(true, KickStart.ModID + ": OnClientSetNewAIProfile - Receive failiure!"); }
        }
        public static void OnServerSetNewAIProfile(NetworkMessage netMsg)
        {
            var reader = new AIProfileChangeMessage();
            netMsg.ReadMessage(reader);
            try
            {
                NetTech find = ManNetTechs.inst.FindTech(reader.netTechID);
                var helper = find.tech.GetHelperInsured();
                helper.TrySetAIProfileRemote(netMsg.GetSender(), reader.profileId);
                Singleton.Manager<ManNetwork>.inst.SendToAllExceptClient(netMsg.conn.connectionId, AIProfileChange, reader, Host);
            }
            catch { DebugTAC_AI.Assert(true, KickStart.ModID + ": OnServerSetNewAIProfile - Receive failiure!"); }
        }

        // AIRetreatMessage
        /// <summary>
        /// sent from both clients and server
        /// </summary>
        /// <param name="team"></param>
        /// <param name="retreat"></param>
        public static void TryBroadcastNewRetreatState(int team, bool retreat)
        {
            if (!ManNetwork.IsNetworked) return;
            try
            {
                var msg = new AIRetreatMessage(team, retreat);
                if (ManNetwork.IsHost)
                {
                    DebugTAC_AI.LogNet("Sent NewRetreatState fan-out to clients (host)");
                    Singleton.Manager<ManNetwork>.inst.SendToAllExceptClient(localConnectionID, AIRetreatRequest, msg, Host);
                }
                else
                {
                    DebugTAC_AI.LogNet("Sent NewRetreatState upstream to host (client)");
                    Singleton.Manager<ManNetwork>.inst.SendToServer(AIRetreatRequest, msg);
                }
            }
            catch { DebugTAC_AI.LogNet(KickStart.ModID + ": Failed to send TryBroadcastNewRetreatState update, shouldn't be too bad in the long run"); }
        }
        public static void OnClientSetRetreatState(NetworkMessage netMsg)
        {
            var reader = new AIRetreatMessage();
            netMsg.ReadMessage(reader);
            try
            {
                AIECore.TeamRetreat(reader.Team, reader.Retreat);
                DebugTAC_AI.LogNet(KickStart.ModID + ": Received new OnClientSetRetreatState update, changing retreat states of (" + reader.Team + ") to retreat " + reader.Retreat);
            }
            catch
            {
                DebugTAC_AI.LogNet(KickStart.ModID + ": OnClientSetRetreatState - receive failiure! Could not decode intake!?");
            }
        }
        public static void OnServerSetRetreatState(NetworkMessage netMsg)
        {
            var reader = new AIRetreatMessage();
            netMsg.ReadMessage(reader);
            try
            {
                AIECore.TeamRetreat(reader.Team, reader.Retreat);
                DebugTAC_AI.LogNet(KickStart.ModID + ": Received new OnServerSetRetreatState update, changing retreat states of (" + reader.Team +") to retreat " + reader.Retreat);
                Singleton.Manager<ManNetwork>.inst.SendToAllExceptClient(netMsg.conn.connectionId, AIRetreatRequest, reader, Host);
            }
            catch
            {
                DebugTAC_AI.LogNet(KickStart.ModID + ": OnServerSetRetreatState - receive failiure! Could not decode intake!?");
            }
        }

        // AIEnemyState
        /// <summary>
        /// SERVER SENT
        /// </summary>
        /// <param name="netTechID"></param>
        /// <param name="smartz"></param>
        public static void TryBroadcastNewEnemyState(uint netTechID, EnemySmarts smartz)
        {
            if (HostExists && ManNetwork.IsHost) try
                {
                    DebugTAC_AI.LogNet("Sent new TryBroadcastNewEnemyState update to all");
                    // REVISED: now sends on AIEnemyType (was AIRetreatRequest) - routes to the enemy-AI handler, not the retreat one
                    Singleton.Manager<ManNetwork>.inst.SendToAllExceptHost(AIEnemyType, new AIEnemySet(netTechID, smartz));
                }
                catch { DebugTAC_AI.LogNet(KickStart.ModID + ": Failed to send TryBroadcastNewEnemyState update, shouldn't be too bad in the long run"); }
        }
        public static void OnClientEnemyAISetup(NetworkMessage netMsg)
        {
            var reader = new AIEnemySet();
            netMsg.ReadMessage(reader);
            try
            {
                NetTech find = ManNetTechs.inst.FindTech(reader.netTechID);
                find.GetComponent<EnemyMind>().CommanderSmarts = (EnemySmarts)reader.enemyType;
                DebugTAC_AI.LogNet(KickStart.ModID + ": OnClientEnemyAISetup - Enemy AI's (" + find.name + ") smarts is " + (EnemySmarts)reader.enemyType);
            }
            catch
            {
                DebugTAC_AI.LogNet(KickStart.ModID + ": OnClientEnemyAISetup - receive failiure! Could not decode intake or input was too early!?");
            }
        }
        public static void OnServerEnemyAISetup(NetworkMessage netMsg)
        {
            DebugTAC_AI.Assert(true, KickStart.ModID + ": OnServerEnemyAISetup should not be sent to host.  This should not be happening.");
        }

        // AIEnemySiege
        public static void TryBroadcastNewEnemySiege(int Team, int TeamTargeted, long HP, bool starting)
        {
            if (HostExists && ManNetwork.IsHost) try
                {
                    DebugTAC_AI.LogNet("Sent new TryBroadcastNewEnemySiege update to all but host");
                    // REVISED: now sends on AIEnemySiege (was AIRetreatRequest) - routes to the siege handler, not the retreat one
                    Singleton.Manager<ManNetwork>.inst.SendToAllExceptHost(AIEnemySiege,
                        new AIEnemyStagedSiege(Team, TeamTargeted, HP, starting));
                }
                catch { DebugTAC_AI.LogNet(KickStart.ModID + ": Failed to send TryBroadcastNewEnemySiege update, shouldn't be too bad in the long run"); }
        }
        public static void OnClientEnemySiegeUpdate(NetworkMessage netMsg)
        {
            var reader = new AIEnemyStagedSiege();
            netMsg.ReadMessage(reader);
            try
            {
                if (reader.Starting)
                    ManEnemySiege.InitSiegeWarning(reader.Team, reader.TeamTargeted, reader.MaxHP);
                else
                    ManEnemySiege.EndSiege();
                DebugTAC_AI.LogNet(KickStart.ModID + ": OnClientEnemySiegeUpdate received.  Attacker is " + reader.Team + " | HP: " + reader.MaxHP + " | is starting: " + reader.Starting);
            }
            catch
            {
                DebugTAC_AI.LogNet(KickStart.ModID + ": OnClientEnemySiegeUpdate receive failiure! Could not decode intake or input was too early!?");
            }
        }
        public static void OnServerEnemySiegeUpdate(NetworkMessage netMsg)
        {
            DebugTAC_AI.Assert(true, KickStart.ModID + ": OnServerEnemySiegeUpdate should not be sent to host.  This should not be happening.");
        }

        public static class Patches
        {
            /// <summary>
            /// Note: Both sides must subscribe to work!
            /// </summary>
            [HarmonyPatch(typeof(NetPlayer), "OnStartClient")]
            static class OnStartClient
            {
                static void Postfix(NetPlayer __instance)
                {
                    // Standard
                    Singleton.Manager<ManNetwork>.inst.SubscribeToClientMessage(__instance.netId, AIRetreatRequest, new ManNetwork.MessageHandler(OnClientSetRetreatState));
                    Singleton.Manager<ManNetwork>.inst.SubscribeToClientMessage(__instance.netId, AIADVTypeChange, new ManNetwork.MessageHandler(OnClientSetNewAIState));
                    Singleton.Manager<ManNetwork>.inst.SubscribeToClientMessage(__instance.netId, AIProfileChange, new ManNetwork.MessageHandler(OnClientSetNewAIProfile));
                    Singleton.Manager<ManNetwork>.inst.SubscribeToClientMessage(__instance.netId, AIEnemyType, new ManNetwork.MessageHandler(OnClientEnemyAISetup));

                    // RTS
                    Singleton.Manager<ManNetwork>.inst.SubscribeToClientMessage(__instance.netId, AIRTSPosCommand, new ManNetwork.MessageHandler(OnClientAcceptRTSCommand));
                    Singleton.Manager<ManNetwork>.inst.SubscribeToClientMessage(__instance.netId, AIRTSPosControl, new ManNetwork.MessageHandler(OnClientAcceptRTSControl));
                    Singleton.Manager<ManNetwork>.inst.SubscribeToClientMessage(__instance.netId, AIRTSAttack, new ManNetwork.MessageHandler(OnClientAcceptRTSAttack));
                    Singleton.Manager<ManNetwork>.inst.SubscribeToClientMessage(__instance.netId, AIEnemySiege, new ManNetwork.MessageHandler(OnClientEnemySiegeUpdate));

                    DebugTAC_AI.Log("Subscribed " + __instance.netId.ToString() + " to AdvancedAI updates from host.");
                }
            }

            [HarmonyPatch(typeof(NetPlayer), "OnStartServer")]
            static class OnStartServer
            {
                static void Postfix(NetPlayer __instance)
                {
                    if (!HostExists)
                    {
                        // Standard
                        Singleton.Manager<ManNetwork>.inst.SubscribeToServerMessage(__instance.netId, AIRetreatRequest, new ManNetwork.MessageHandler(OnServerSetRetreatState));
                        Singleton.Manager<ManNetwork>.inst.SubscribeToServerMessage(__instance.netId, AIADVTypeChange, new ManNetwork.MessageHandler(OnServerSetNewAIState));
                        Singleton.Manager<ManNetwork>.inst.SubscribeToServerMessage(__instance.netId, AIProfileChange, new ManNetwork.MessageHandler(OnServerSetNewAIProfile));
                        Singleton.Manager<ManNetwork>.inst.SubscribeToServerMessage(__instance.netId, AIEnemyType, new ManNetwork.MessageHandler(OnServerEnemyAISetup));

                        // RTS
                        Singleton.Manager<ManNetwork>.inst.SubscribeToServerMessage(__instance.netId, AIRTSPosCommand, new ManNetwork.MessageHandler(OnServerAcceptRTSCommand));
                        Singleton.Manager<ManNetwork>.inst.SubscribeToServerMessage(__instance.netId, AIRTSPosControl, new ManNetwork.MessageHandler(OnServerAcceptRTSControl));
                        Singleton.Manager<ManNetwork>.inst.SubscribeToServerMessage(__instance.netId, AIRTSAttack, new ManNetwork.MessageHandler(OnServerAcceptRTSAttack));
                        Singleton.Manager<ManNetwork>.inst.SubscribeToServerMessage(__instance.netId, AIEnemySiege, new ManNetwork.MessageHandler(OnServerEnemySiegeUpdate));

                        DebugTAC_AI.Log("Host started, hooked AdvancedAI update broadcasting to " + __instance.netId.ToString());
                        Host = __instance.netId;
                        HostExists = true;
                    }
                }
            }
        }
    }
}
