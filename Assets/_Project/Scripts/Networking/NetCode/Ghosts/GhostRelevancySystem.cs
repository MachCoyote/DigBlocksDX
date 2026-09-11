using System;
using System.Collections.Generic;
using DigBlocks.ChunkProtocol;
using DigBlocks.Simulation;
using Unity.Entities;
using Unity.NetCode;

namespace DigBlocks.Networking.NetCode
{
    /// <summary>
    /// Replicates an entity only to the peers whose chunk interest actually covers it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the single most important performance property of the entity foundation. Without it
    /// every mob in the world is sent to every client, and snapshot cost grows with the size of the
    /// world. With it, mob count scales with the world while per-client cost scales with view
    /// distance, which is the only shape that holds up.
    /// </para>
    /// <para>
    /// Relevancy is derived from the same per-peer interest chunk streaming uses, so a client is
    /// never sent a mob standing in a chunk it does not have. The two cannot drift, because there is
    /// only one opinion about what a peer can see.
    /// </para>
    /// </remarks>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ServerEntityResidencySystem))]
    [UpdateBefore(typeof(GhostSendSystem))]
    public partial class ServerGhostRelevancySystem : SystemBase
    {
        private readonly List<ChunkStreamingServer.PeerInterest> interests = new List<ChunkStreamingServer.PeerInterest>();
        private readonly Dictionary<ulong, int> networkIdByPeer = new Dictionary<ulong, int>();
        private Action<List<ChunkStreamingServer.PeerInterest>> readInterests;
        private EntitySimulationDistance distance;

        public int RelevantPairCount { get; private set; }

        protected override void OnCreate()
        {
            RequireForUpdate<SessionActive>();
            Enabled = false;
        }

        internal void Configure(Action<List<ChunkStreamingServer.PeerInterest>> interestReader,
            EntitySimulationDistance simulationDistance)
        {
            readInterests = interestReader ?? throw new ArgumentNullException(nameof(interestReader));
            distance = simulationDistance;
            Enabled = true;
        }

        protected override void OnUpdate()
        {
            var context = World.GetExistingSystemManaged<SessionContextSystem>().Context;
            if (context == null) return;
            if (!SystemAPI.TryGetSingletonRW<GhostRelevancy>(out var relevancy)) return;

            readInterests(interests);
            if (interests.Count == 0)
            {
                //no bound peer has declared interest yet. Leaving relevancy disabled sends everything,
                //which is the safe direction: a missing ghost is a bug, a surplus one is bandwidth.
                relevancy.ValueRW.GhostRelevancyMode = GhostRelevancyMode.Disabled;
                RelevantPairCount = 0;
                return;
            }

            MapConnections(context);
            relevancy.ValueRW.GhostRelevancyMode = GhostRelevancyMode.SetIsRelevant;
            var set = relevancy.ValueRW.GhostRelevancySet;
            set.Clear();

            int pairs = 0;
            foreach (var (residency, ghost) in SystemAPI.Query<RefRO<ChunkResidency>, RefRO<GhostInstance>>())
            {
                var address = residency.ValueRO.Address;
                for (int i = 0; i < interests.Count; i++)
                {
                    var interest = interests[i].Interest;
                    //the static test avoids building a narrowed interest per peer per tick; it is the
                    //same volume residency enumerates, asked about one address instead of all of them.
                    if (!ChunkInterest.Contains(interest.Anchor,
                        Math.Min(distance.Horizontal, interest.HorizontalRadius),
                        Math.Min(distance.Vertical, interest.VerticalRadius), address)) continue;
                    if (!networkIdByPeer.TryGetValue(interests[i].PeerId, out int networkId)) continue;
                    set.TryAdd(new RelevantGhostForConnection(networkId, ghost.ValueRO.ghostId), 1);
                    pairs++;
                }
            }
            RelevantPairCount = pairs;
        }

        //the bulk companion keys peers by its own id; NetCode keys relevancy by NetworkId. The
        //session already holds the connection-to-peer mapping, so this is the only place they meet.
        private void MapConnections(SessionContext context)
        {
            networkIdByPeer.Clear();
            foreach (var (networkId, entity) in SystemAPI.Query<RefRO<NetworkId>>().WithEntityAccess())
                if (context.ServerConnections.TryGetValue(entity, out ulong peerId))
                    networkIdByPeer[peerId] = networkId.ValueRO.Value;
        }
    }
}
