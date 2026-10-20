using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Game.Core.Serialization;
using Game.Core.Map.Simulation;
using Game.Core.Map.Simulation.Clustering;
using Game.Core.Modding;
using Game.Core.Simulation;
using HarmonyLib;

namespace Shapez2Multiplayer
{
    /// <summary>Experimental fixed-step adapter. Determinism requires in-engine validation.</summary>
    public static class CoopSimulation
    {
        public static void Advance()
        {
            var game = Shapez2Multiplayer.GameSessionOrchestrator;
            if (game == null) throw new InvalidOperationException("No world for simulation step");
            game.FinalizeLogicUpdate();
            var delta = Ticks.OneSecond / 20;
            var config = new SimulationUpdateConfiguration(Array.Empty<SimulationLOD>(), TimeSpan.MaxValue, 0);
            var strategy = new ProcessingUpdateStrategy(SimulationConstants.MaxSimulationUpdateDelta,
                SimulationConstants.MaxSimulatorIncrement, new[] { delta });
            game.Simulator.SynchronousUpdate(delta, config, strategy);
            game.ProcessSimulatorObservers();
        }

        public static IReadOnlyDictionary<string, byte[]> Capture()
        {
            PrepareSnapshot();
            var game = Shapez2Multiplayer.GameSessionOrchestrator;
            if (game == null) throw new InvalidOperationException("No world to snapshot");
            var context = new SavegameSerializer.GameContext(Shapez2Multiplayer.MapModel, game.LocalPlayer,
                Shapez2Multiplayer.Research, game.ResourcesMap,
                game.DependencyContainer.Resolve<IModdingFramework>(), game.GameStatisticsTracker);
            Shapez2Multiplayer.Savegame.UpdateData(context);
            return new SavegameSerializer(game.DataSerializers, Shapez2Multiplayer.logger).SerializeSavegame(
                Shapez2Multiplayer.Savegame, context, game.MapSerializer, game.SimulationStateSerializer, game.Viewport);
        }

        public static void PrepareSnapshot()
        {
            var game = Shapez2Multiplayer.GameSessionOrchestrator;
            game.FinalizeLogicUpdate();
            var config = new SimulationUpdateConfiguration(Array.Empty<SimulationLOD>(), TimeSpan.MaxValue, 0);
            game.Simulator.SynchronousUpdate(Ticks.Zero, config, new ProcessingPrepareSaveStrategy());
            game.ProcessSimulatorObservers();
        }

        // Includes production buffers, fluids, logic, trains/cargo and research. Canonical outer
        // order removes map enumeration differences; serializers themselves still require QA.
        public static byte[] Digest()
        {
            var game = Shapez2Multiplayer.GameSessionOrchestrator;
            game.FinalizeLogicUpdate();
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, true);
            var visitor = new BinarySerializationVisitor(true, false, Savegame.CurrentVersion,
                output, game.DataSerializers, Shapez2Multiplayer.logger);
            foreach (var island in Shapez2Multiplayer.MapModel.Islands.OrderBy(i => i.Position.x).ThenBy(i => i.Position.y).ThenBy(i => i.Position.z))
            {
                Encoding.Encode(island.Position, output);
                Encoding.Encode(island.Transform.Rotation, output);
                Encoding.Encode(island.Definition.Id, output);
                island.State.Sync(visitor);
            }
            foreach (var building in Shapez2Multiplayer.MapModel.Buildings.OrderBy(b => b.Tile_G.x).ThenBy(b => b.Tile_G.y).ThenBy(b => b.Tile_G.z))
            {
                Encoding.Encode(building.Tile_G, output);
                Encoding.Encode(building.Rotation_G, output);
                Encoding.Encode(building.Definition.Id, output);
                building.State.Sync(visitor);
                building.Configuration?.Sync(visitor);
            }
            var lut = new SavegameStringLUT();
            var blobs = new DeferredSavegameBlobWriter(Shapez2Multiplayer.Savegame.BinaryDataCheckpoints,
                lut, game.DataSerializers, Shapez2Multiplayer.logger);
            game.MapSerializer.MapSimulationSerializer.Serialize(blobs);
            game.SimulationStateSerializer.SerializeSimulationState(blobs, Shapez2Multiplayer.MapModel);
            Shapez2Multiplayer.Research.Serialize(blobs);
            lut.Serialize(blobs);
            foreach (var entry in blobs.SerializedFileContents.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                writer.Write(entry.Key); writer.Write(entry.Value.Length); writer.Write(entry.Value);
            }
            writer.Flush();
            using var hash = SHA256.Create();
            return hash.ComputeHash(output.ToArray());
        }
    }
    [HarmonyPatch(typeof(GameSessionOrchestrator), "StartLogicUpdate")]
    internal static class CoopSimulationStepPatch
    {
        [HarmonyPrefix] private static bool Prefix() => !CoopSession.Active;
    }
    [HarmonyPatch(typeof(GameSessionOrchestrator), "StartPredictionUpdate")]
    internal static class CoopPredictionPatch
    {
        [HarmonyPrefix] private static bool Prefix() => !CoopSession.Active;
    }
    // Joined clients are replicas; they must never write a host's save UID to local disk.
    [HarmonyPatch(typeof(GameSessionOrchestrator), "TrySaveCurrent")]
    internal static class CoopClientSavePatch
    {
        [HarmonyPrefix] private static bool Prefix(ref bool __result)
        {
            if (!MultiplayerCore.Client && !CoopSession.ReplicaWorld) return true;
            __result = false; return false;
        }
    }
    [HarmonyPatch(typeof(GameSessionOrchestrator), nameof(GameSessionOrchestrator.Dispose))]
    internal static class CoopReplicaDisposePatch
    {
        [HarmonyPostfix] private static void Postfix(GameSessionOrchestrator __instance)
        {
            // Loading a replica disposes the menu's background world first.
            // Only disposal of the actual replica releases the save guard.
            if (!ReferenceEquals(CoopSession.ReplicaSession, __instance)) return;
            CoopSession.ReplicaSession = null;
            CoopSession.ReplicaWorld = false;
        }
    }
}
