using System;
using System.Linq;
using Game.Core.Coordinates;
using UnityEngine;

namespace Shapez2Multiplayer
{
    internal static class CoopQaProduction
    {
        public static bool Cleared, Refilled;
        public static BuildingModel Producer => Shapez2Multiplayer.MapModel.Buildings
            .Single(b => b.Configuration is ItemProducerConfiguration);
        public static bool HasOutput => Producer.State.As<ItemProducerSimulationState>().OutputLaneState.Item != null;

        public static void Seed()
        {
            var game = Shapez2Multiplayer.GameSessionOrchestrator;
            // LoadSession can finish while the newly displayed world has a calculation
            // in flight. Map edits must follow the same barrier as runtime intents.
            game.FinalizeLogicUpdate();
            var mode = Shapez2Multiplayer.Mode;
            var shapeHash = string.Concat(Enumerable.Repeat(
                $"{mode.ShapesConfiguration.Parts.First().Code}{mode.ShapeColorScheme.Colors.First().Code}",
                mode.ShapesConfiguration.PartCount));
            if (!game.DependencyContainer.Resolve<IShapeDefinitionFactory>().TryCreateShapeDefinition(shapeHash, out var shape))
                throw new InvalidOperationException("QA shape rejected");
            var config = new ItemProducerConfiguration { ResourceItem = game.DependencyContainer.Resolve<IShapeRegistry>().GetItem(shape.Id) };
            var definition = mode.Buildings.GetDefinitionGroup(mode.Buildings.SandboxShapeProducerId).Definitions.First();
            foreach (var island in Shapez2Multiplayer.MapModel.Islands)
            for (short y = -16; y <= 16; y++)
            for (short x = -16; x <= 16; x++)
            {
                var placement = new PlaceBuildingPayload(island.Id, definition, config,
                    new IslandTileTransform(new IslandTileCoordinate(x, y, 0), GridRotation.NoRotate), forceAllowPlace: true);
                var action = new ActionModifyBuildings(Shapez2Multiplayer.MapModel, game.LocalPlayer,
                    new ModifyBuildingsPayload(new[] { placement }));
                if (!action.IsPossible(Shapez2Multiplayer.InteractionMode)) continue;
                // QA setup only, before hosting. No forced intent is submitted over the network.
                if (!Shapez2Multiplayer.PlayerActions.ExecuteActionImmediately_INTERNAL(action, out _))
                    throw new InvalidOperationException("QA producer placement failed");
                Debug.Log("QA:PRODUCER_SEEDED");
                return;
            }
            throw new InvalidOperationException("No valid production fixture position");
        }
        public static void ObserveClear(IPlayerAction action, bool accepted)
        {
            if (!accepted || !(action is ClearBuildingContentPlayerAction)) return;
            if (HasOutput) throw new InvalidOperationException("Production buffer did not clear");
            Cleared = true;
            Debug.Log("QA:OUTPUT_CLEARED step=" + CoopSession.StepCount);
        }
        public static void Tick(string role)
        {
            if (!Cleared || Refilled || !HasOutput) return;
            Refilled = true;
            Debug.Log("QA:OUTPUT_REFILLED role=" + role + " step=" + CoopSession.StepCount);
        }
    }
}
