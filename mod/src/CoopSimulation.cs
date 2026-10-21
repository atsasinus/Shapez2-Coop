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
using Game.Core.Coordinates;

namespace Shapez2Multiplayer
{
    /// <summary>Experimental fixed-step adapter. Determinism requires in-engine validation.</summary>
    public static class CoopSimulation
    {
        private static CoopDigestIndex? digestIndex;
#if COOP_QA
        public static byte? QaWorkers;
#endif
        private static readonly byte WorkerCount=(byte)Math.Min(8,Math.Max(1,Environment.ProcessorCount/2));
        private static readonly Dictionary<int,ProcessingUpdateStrategy> Strategies=new Dictionary<int,ProcessingUpdateStrategy>();
        public static void ResetDigestIndex() {digestIndex?.Dispose();digestIndex=null;}
        public static void Advance()
        {
            var game = Shapez2Multiplayer.GameSessionOrchestrator;
            if (game == null) throw new InvalidOperationException("No world for simulation step");
            game.FinalizeLogicUpdate();
            int hz = CoopSession.SimulationHz;
            if (!Shapez2Coop.Protocol.SessionPayloads.SupportedRate(hz) || Ticks.OneSecond.Value % hz != 0)
                throw new InvalidOperationException("Simulation frequency is incompatible with the game's tick grid");
            var delta = Ticks.OneSecond / hz;
            byte workers=WorkerCount;
#if COOP_QA
            workers=QaWorkers??workers;
#endif
            var config = new SimulationUpdateConfiguration(Array.Empty<SimulationLOD>(), TimeSpan.MaxValue, workers);
            if(!Strategies.TryGetValue(hz,out var strategy))
            {strategy=new ProcessingUpdateStrategy(SimulationConstants.MaxSimulationUpdateDelta,SimulationConstants.MaxSimulatorIncrement,new[]{delta});Strategies.Add(hz,strategy);}
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
        public static byte[] Digest(int partition = -1)
        {
            var game = Shapez2Multiplayer.GameSessionOrchestrator;
            game.FinalizeLogicUpdate();
            if(partition < -1 || partition >= Shapez2Coop.Protocol.DigestSchedule.Partitions)throw new InvalidDataException("Invalid digest partition");
            var map=Shapez2Multiplayer.MapModel;
            if(digestIndex==null || !digestIndex.Owns(map)){ResetDigestIndex();digestIndex=new CoopDigestIndex(map);}
            digestIndex.Validate();
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, true);
            var visitor = new BinarySerializationVisitor(true, false, Savegame.CurrentVersion,
                output, game.DataSerializers, Shapez2Multiplayer.logger);
#if COOP_QA
            long mark=0;
            void Trace(string part)
            {
                if(partition<0)
                {
                    writer.Flush();using var h=SHA256.Create();
                    UnityEngine.Debug.Log("QA:DIGEST_PART "+part+" bytes="+(output.Position-mark)+" hash="+BitConverter.ToString(h.ComputeHash(output.GetBuffer(),(int)mark,(int)(output.Position-mark))));
                    mark=output.Position;
                }
            }
#endif
            writer.Write("S2C-state-v2");writer.Write(partition);writer.Write(map.BuildingCount);writer.Write(map.Islands.Count());
            var islandSet=partition<0?map.Islands.OrderBy(i=>i.Position.x).ThenBy(i=>i.Position.y).ThenBy(i=>i.Position.z):(System.Collections.Generic.IEnumerable<IslandModel>)digestIndex.Islands(partition);
            foreach (var indexedIsland in islandSet)
            {
                var island=indexedIsland;
                if(partition>=0 && !map.TryGetIsland(indexedIsland.Position,out island))throw new InvalidDataException("Indexed island missing");
                writer.Write(island.Position.x);writer.Write(island.Position.y);writer.Write(island.Position.z);
                writer.Write((byte)island.Transform.Rotation.Value);
                writer.Write(island.Definition.Id.Name);
                island.State.Sync(visitor);
                island.Configuration?.Sync(visitor);
            }
#if COOP_QA
            Trace("islands");
#endif
            writer.Write("buildings");
            var buildingSet=partition<0?map.Buildings.OrderBy(b=>b.Tile_G.x).ThenBy(b=>b.Tile_G.y).ThenBy(b=>b.Tile_G.z):(System.Collections.Generic.IEnumerable<BuildingModel>)digestIndex.Buildings(partition);
            foreach (var indexedBuilding in buildingSet)
            {
                var building=indexedBuilding;
                if(partition>=0 && !map.TryGetBuilding(indexedBuilding.Tile_G,out building))throw new InvalidDataException("Indexed building missing");
                writer.Write(building.Tile_G.x);writer.Write(building.Tile_G.y);writer.Write(building.Tile_G.z);
                writer.Write((byte)building.Rotation_G.Value);
                writer.Write(building.Definition.Id.Name);
                building.State.Sync(visitor);
                building.Configuration?.Sync(visitor);
            }
#if COOP_QA
            Trace("buildings");
#endif
            var lut = new SavegameStringLUT();
            var blobs = new DeferredSavegameBlobWriter(Shapez2Multiplayer.Savegame.BinaryDataCheckpoints,
                lut, game.DataSerializers, Shapez2Multiplayer.logger);
            var mapSerializer=game.MapSerializer.MapSimulationSerializer;
            var trainSystem=mapSerializer.TrainSystem;
            blobs.Write("maps/main/trains.bin",v=>mapSerializer.TrainsSerializer.Serialize(trainSystem.TrainsSimulation,v));
            blobs.Write("maps/main/cargo.bin",v=>WriteCargo(v,trainSystem.CargoExchanger));
            game.SimulationStateSerializer.SerializeSimulationState(blobs, Shapez2Multiplayer.MapModel);
            Shapez2Multiplayer.Research.Serialize(blobs);
            lut.Serialize(blobs);
            foreach (var entry in blobs.SerializedFileContents.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                writer.Write(entry.Key); writer.Write(entry.Value.Length); writer.Write(entry.Value);
#if COOP_QA
                Trace(entry.Key);
#endif
            }
            writer.Flush();
            using var hash = SHA256.Create();
            output.Position=0;
            return hash.ComputeHash(output);
        }
        private static void WriteCargo(ISerializationVisitor v,CargoExchangingOrchestrator cargo)
        {
            v.WriteBlob((Action)(()=>{
                WriteCargoMap(v,cargo.ShapeCargoLoaderUnloader.CargoLoaderMap,x=>x.State);
                WriteCargoMap(v,cargo.FluidCargoLoaderUnloader.CargoLoaderMap,x=>x.State);
                WriteCargoMap(v,cargo.ShapeCargoLoaderUnloader.CargoUnloaderMap,x=>x.State);
                WriteCargoMap(v,cargo.FluidCargoLoaderUnloader.CargoUnloaderMap,x=>x.State);
                WriteCargoMap(v,cargo.ShapeCargoLoaderUnloader.CargoTransferrerMap,x=>x.State);
                WriteCargoMap(v,cargo.FluidCargoLoaderUnloader.CargoTransferrerMap,x=>x.State);
            }));
        }
        private static void WriteCargoMap<T>(ISerializationVisitor v,IReadOnlyDictionary<GlobalChunkCoordinate,T> map,Func<T,ISyncable> state)
        {
            var key=v.GetSerializer<GlobalChunkCoordinate>();v.WriteInt_4(map.Count);
            foreach(var entry in map.OrderBy(e=>e.Key.x).ThenBy(e=>e.Key.y).ThenBy(e=>e.Key.z))
            {key.Serialize(entry.Key);var item=state(entry.Value);v.WriteBlob((Action)(()=>item.Sync(v)));}
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
        [HarmonyPrefix] private static void Prefix() => CoopSimulation.ResetDigestIndex();
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
