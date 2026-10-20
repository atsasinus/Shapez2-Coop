using System;
using System.Linq;
using Game.Core.Coordinates;
using Shapez2Multiplayer.Packets;
using UnityEngine;

namespace Shapez2Multiplayer
{
    // Exercises the actual Harmony scheduling hook, wire codec, host revision gate
    // and client commit adapter. No direct map writes or forced placements.
    internal sealed class CoopQaActions
    {
        private int phase;
        private ulong revision, phaseStep, oldRevision;
        private GlobalTileCoordinate tile;
        private PlaceBuildingPayload placement;
        private byte[] staleDelete;
        private BuildingId replacement;
        public bool Complete => phase == 9;

        private static ActionModifyBuildings Place(PlaceBuildingPayload payload) =>
            new ActionModifyBuildings(Shapez2Multiplayer.MapModel,
                Shapez2Multiplayer.GameSessionOrchestrator.LocalPlayer,
                new ModifyBuildingsPayload(new[] { payload }));
        private static ActionModifyBuildings Delete(BuildingModel building) =>
            new ActionModifyBuildings(Shapez2Multiplayer.MapModel,
                Shapez2Multiplayer.GameSessionOrchestrator.LocalPlayer,
                new ModifyBuildingsPayload(new[] { new DeleteBuildingPayload(building.Id, false) }));

        public void Tick()
        {
            var map = Shapez2Multiplayer.MapModel;
            if (phase == 0)
            {
                bool found = false;
                foreach (var island in map.Islands.OrderBy(i => i.Position.x).ThenBy(i => i.Position.y))
                {
                    for (int y = -16; y <= 16 && !found; y++)
                    for (int x = -16; x <= 16 && !found; x++)
                    {
                        var coordinate = new IslandTileCoordinate((short)x, (short)y, 0);
                        var candidate = new PlaceBuildingPayload(island.Id, Shapez2Multiplayer.Mode.Buildings.ForwardBelt,
                            null, new IslandTileTransform(coordinate, GridRotation.NoRotate));
                        if (!Place(candidate).IsPossible(Shapez2Multiplayer.InteractionMode)) continue;
                        placement = candidate; tile = coordinate.ToGlobal(island.Position); found = true;
                    }
                    if (found) break;
                }
                if (!found) throw new InvalidOperationException("No ordinary belt placement found on initial island");
                Submit(Place(placement));
                if (map.TryGetBuilding(tile, out _)) throw new InvalidOperationException("Client mutated world optimistically");
                phase = 1;
            }
            else if (phase == 1 && Committed())
            {
                if (!map.TryGetBuilding(tile, out _)) throw new InvalidOperationException("Accepted placement absent");
                Debug.Log("QA:PLACE_COMMITTED revision=" + CoopSession.EditRevision);
                phaseStep = CoopSession.StepCount; phase = 2;
            }
            else if (phase == 2 && CoopSession.StepCount >= phaseStep + 100)
            {
                if (!map.TryGetBuilding(tile, out var original)) throw new InvalidOperationException("Placed belt disappeared");
                var deletion = Delete(original);
                staleDelete = PacketExtensions.Encode(new PlayerActionPacket(deletion));
                oldRevision = CoopSession.EditRevision;
                Submit(deletion);
                if (!map.TryGetBuilding(tile, out _)) throw new InvalidOperationException("Optimistic deletion");
                phase = 3;
            }
            else if (phase == 3 && Committed())
            {
                if (map.TryGetBuilding(tile, out _)) throw new InvalidOperationException("Accepted deletion not applied");
                Debug.Log("QA:DELETE_COMMITTED revision=" + CoopSession.EditRevision);
                Submit(Place(placement)); phase = 4;
            }
            else if (phase == 4 && Committed())
            {
                if (!map.TryGetBuilding(tile, out var building)) throw new InvalidOperationException("Replacement absent");
                replacement = building.Id;
                revision = CoopSession.EditRevision;
                if (!CoopSession.QaSubmitAtRevision(staleDelete, oldRevision)) throw new InvalidOperationException("Stale intent not sent");
                phaseStep = CoopSession.StepCount; phase = 5;
            }
            else if (phase == 5 && CoopSession.StepCount >= phaseStep + 20)
            {
                if (CoopSession.EditRevision != revision || !map.TryGetBuilding(tile, out var building) ||
                    !building.Id.Equals(replacement)) throw new InvalidOperationException("Stale deletion changed replacement");
                Debug.Log("QA:STALE_DELETE_PRESERVED revision=" + revision);
                Submit(Delete(building)); phase = 6;
            }
            else if (phase == 6 && Committed())
            {
                if (map.TryGetBuilding(tile, out _)) throw new InvalidOperationException("Final deletion not applied");
                phase = 7;
            }
            else if (phase == 7)
            {
                if (!CoopQaProduction.HasOutput) throw new InvalidOperationException("Production fixture never produced an item");
                Submit(new ClearBuildingContentPlayerAction(map, Shapez2Multiplayer.GameSessionOrchestrator.LocalPlayer,
                    new[] { CoopQaProduction.Producer.Id }));
                if (!CoopQaProduction.HasOutput) throw new InvalidOperationException("Optimistic buffer clear");
                phase = 8;
            }
            else if (phase == 8 && Committed() && CoopQaProduction.Refilled)
            {
                phase = 9; Debug.Log("QA:ACTIONS_PASS revision=" + CoopSession.EditRevision);
            }
        }
        private void Submit(IPlayerAction action)
        {
            revision = CoopSession.EditRevision;
            phaseStep = CoopSession.StepCount;
            if (!Shapez2Multiplayer.PlayerActions.TryScheduleAction(action)) throw new InvalidOperationException("QA action was not queued");
            if (CoopSession.EditRevision != revision) throw new InvalidOperationException("Local commit occurred before host response");
        }
        private bool Committed()
        {
            if (CoopSession.EditRevision == revision + 1) return true;
            if (CoopSession.StepCount > phaseStep + 100) throw new InvalidOperationException("QA action commit timeout");
            return false;
        }
    }
}
