using System;
using System.Linq;
using Game.Core.Coordinates;
using Shapez2Multiplayer.Packets;
using UnityEngine;

namespace Shapez2Multiplayer
{
    internal sealed class CoopQaRace
    {
        private bool prepared, sent, complete;
        private byte[] intent;
        private GlobalTileCoordinate tile;
        private ulong target;
        private int initialCount;
        public bool Tick(string role)
        {
            if (complete) return true;
            var map = Shapez2Multiplayer.MapModel;
            if (!prepared)
            {
                if (CoopSession.EditRevision != 0) throw new InvalidOperationException("Race did not start at the initial revision");
                foreach (var island in map.Islands.OrderBy(i => i.Position.x).ThenBy(i => i.Position.y).ThenBy(i => i.Position.z))
                {
                    for (short y = -16; y <= 16 && !prepared; y++)
                    for (short x = -16; x <= 16 && !prepared; x++)
                    {
                        var coordinate = new IslandTileCoordinate(x, y, 0);
                        var place = new PlaceBuildingPayload(island.Id, Shapez2Multiplayer.Mode.Buildings.ForwardBelt,
                            null, new IslandTileTransform(coordinate, GridRotation.NoRotate));
                        var action = new ActionModifyBuildings(map, Shapez2Multiplayer.GameSessionOrchestrator.LocalPlayer,
                            new ModifyBuildingsPayload(new[] { place }));
                        if (!action.IsPossible(Shapez2Multiplayer.InteractionMode)) continue;
                        intent = PacketExtensions.Encode(new PlayerActionPacket(action));
                        tile = coordinate.ToGlobal(island.Position); prepared = true;
                    }
                    if (prepared) break;
                }
                if (!prepared) throw new InvalidOperationException("No shared race placement found");
                initialCount = map.BuildingCount;
                target = CoopSession.SnapshotStep + 100;
                Debug.Log("QA:RACE_PREPARED role=" + role + " target=" + target + " tile=" + tile);
            }
            if (!sent && CoopSession.StepCount >= target)
            {
                // Both writers retain the same observed revision, irrespective of the
                // order in which their messages reach the authoritative main thread.
                if (!CoopSession.QaSubmitAtRevision(intent, 0)) throw new InvalidOperationException("Race intent was not queued");
                sent = true;
            }
            if (!sent || CoopSession.StepCount < target + 20) return false;
            if (CoopSession.EditRevision != 1 || map.BuildingCount != initialCount + 1 || !map.TryGetBuilding(tile, out _))
                throw new InvalidOperationException("Conflicting intents did not produce exactly one shared placement");
            complete = true;
            Debug.Log("QA:RACE_SINGLE_COMMIT role=" + role + " revision=1 tile=" + tile);
            return true;
        }
    }
}
