using System;
using System.Collections.Generic;
using System.Linq;
using Game.Core.Coordinates;
using Game.Placement.Data;
using Game.Placement.Processing;
using HarmonyLib;
using Shapez2Coop.Protocol;

namespace Shapez2Multiplayer
{
    // These records contain render descriptions only. They never enter the map,
    // simulation graph, placement caches, research state or save serializer.
    public static class CoopPrediction
    {
        private sealed class Preview
        {
            public readonly List<SmartBuildingBlueprintRenderer.DrawData> Buildings = new List<SmartBuildingBlueprintRenderer.DrawData>();
            public readonly List<IslandPreviewRenderData> Islands = new List<IslandPreviewRenderData>();
            public double Created;
#if COOP_QA
            public bool Checked;
#endif
            public int Count => Buildings.Count + Islands.Count;
        }
        private const int MaxVisuals = 2048;
        private static PredictionLedger<Preview>? pending;
        private static int visualCount;
        private static double lastUpdate;
        public static int PendingCount => pending?.Count ?? 0;
        public static int VisualCount => visualCount;
        public static ulong DrawnObjects { get; private set; }
        public static uint Confirmed, RolledBack;
        public static uint ForeignConfirmationsIgnored;
        public static void Initialize(uint player)
        { Clear(); pending = new PredictionLedger<Preview>(player); Confirmed = RolledBack = ForeignConfirmationsIgnored = 0; DrawnObjects = 0; }
        public static void Clear()
        { pending?.Clear(); visualCount = 0; lastUpdate = UnityEngine.Time.realtimeSinceStartupAsDouble; }
        public static void Track(ulong request, IPlayerAction? action)
        {
            if (!MultiplayerCore.Client || !CoopSettings.Current.VisualPrediction || action == null ||
                pending == null || pending.Count >= pending.Capacity || visualCount >= MaxVisuals) return;
            try
            {
                var preview = new Preview { Created = UnityEngine.Time.realtimeSinceStartupAsDouble };
                Collect(action, preview, 0);
                if (preview.Count > 0 && pending.TryAdd(request, preview))
                {
                    visualCount += preview.Count;
#if COOP_QA
                    UnityEngine.Debug.Log("QA:PREDICTION_CREATED request=" + request + " visuals=" + preview.Count);
#endif
                }
            }
            catch (Exception ex)
            {
                // Failure to build presentation data must not execute or cancel an intent.
                Shapez2Multiplayer.logger.Warning?.Log("Preview unavailable: " + ex.Message);
            }
        }
        private static bool Room(Preview preview) => preview.Count + visualCount < MaxVisuals;
        private static void Collect(IPlayerAction action, Preview preview, int depth)
        {
            if (depth > 16 || !Room(preview)) return;
            var map = Shapez2Multiplayer.MapModel;
            if (action is ActionModifyBuildings buildings)
            {
                foreach (var place in buildings.Data.Place)
                {
                    if (!Room(preview)) break;
                    if (map.TryGetIsland(place.IslandId, out var island)) AddBuilding(preview, place, island.Position);
                }
                foreach (var deletion in buildings.Data.Delete)
                {
                    if (!Room(preview)) break;
                    if (map.TryGetBuilding(deletion.BuildingId, out var building))
                    {
                        var transform = building.Transform;
                        preview.Buildings.Add(new SmartBuildingBlueprintRenderer.DrawData(building.Definition,
                            in transform, PlacementAllowability.InvalidPlacement));
                    }
                }
            }
            else if (action is ActionModifyIsland islands)
            {
                foreach (var place in islands.Data.Place)
                {
                    if (!Room(preview)) break;
                    preview.Islands.Add(new IslandPreviewRenderData(place.Definition,
                        new GlobalChunkTransform(place.Origin_GC, place.Rotation), place.Configuration,
                        PlacementAllowability.ValidPlacementButDisplaysWarning));
                    foreach (var building in place.PlaceBuildings)
                    { if (!Room(preview)) break; AddBuilding(preview, building, place.Origin_GC); }
                }
                foreach (var deletion in islands.Data.Delete)
                {
                    if (!Room(preview)) break;
                    if (map.TryGetIsland(deletion.IslandId, out var island))
                        preview.Islands.Add(new IslandPreviewRenderData(island.Definition, island.Transform,
                            island.Configuration, PlacementAllowability.InvalidPlacement));
                }
            }
            else if (action is CombinedUndoablePlayerAction combined)
                foreach (var child in combined.Actions)
                { if (!Room(preview)) break; Collect(child, preview, depth + 1); }
        }
        private static void AddBuilding(Preview preview, PlaceBuildingPayload place, GlobalChunkCoordinate origin)
        {
            var transform = place.Transform_I.ToGlobal(origin);
            preview.Buildings.Add(new SmartBuildingBlueprintRenderer.DrawData(place.Definition, in transform,
                PlacementAllowability.ValidPlacementButDisplaysWarning));
        }
        public static void Confirm(uint origin, ulong request)
        {
            if (pending != null && origin != pending.LocalPlayer && pending.Contains(request))
            {
                ++ForeignConfirmationsIgnored;
#if COOP_QA
                UnityEngine.Debug.Log("QA:FOREIGN_COMMIT_PRESERVED request=" + request);
#endif
            }
            if (pending == null || !pending.Confirm(origin, request, out var preview)) return;
            visualCount -= preview.Count; ++Confirmed;
#if COOP_QA
            UnityEngine.Debug.Log("QA:PREDICTION_CONFIRMED request=" + request);
#endif
        }
        public static void Reject(ulong request)
        {
            if (pending == null || !pending.Reject(request, out var preview)) return;
            visualCount -= preview.Count; ++RolledBack;
#if COOP_QA
            UnityEngine.Debug.Log("QA:PREDICTION_ROLLED_BACK request=" + request);
#endif
        }
        public static void Update()
        {
            if (pending == null || !MultiplayerCore.Client || CoopSession.Faulted) return;
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            // A snapshot join deliberately pauses host command processing. That
            // interval must not consume the deadline of an already pending intent.
            if (!CoopSession.Ready)
                foreach (var preview in pending.Values) preview.Created += Math.Max(0, now - lastUpdate);
            lastUpdate = now;
            if (pending.Values.Any(p => now - p.Created > 15))
                CoopSession.Fail("Host did not resolve a pending visual intent");
        }
        public static void Draw(FrameDrawOptions options)
        {
            if (!MultiplayerCore.Client || !CoopSettings.Current.VisualPrediction || pending == null ||
                CoopSession.Faulted || Shapez2Multiplayer.GameSessionOrchestrator == null) return;
            try
            {
                foreach (var preview in pending.Values)
                {
#if COOP_QA
                    var before = preview.Checked || CoopQaPerf.Instance!=null ? null : CoopSimulation.Digest();
#endif
                    if (preview.Buildings.Count > 0) SmartBuildingBlueprintRenderer.Draw(options, preview.Buildings, null);
                    if (preview.Islands.Count > 0)
                        Shapez2Multiplayer.GameSessionOrchestratorDependencyContainer.Resolve<IIslandPreviewDrawer>()
                            .Draw(Shapez2Multiplayer.MapModel.Layout, options, preview.Islands, false, false, false);
                    DrawnObjects += (ulong)preview.Count;
#if COOP_QA
                    if (before != null)
                    {
                        if (!before.SequenceEqual(CoopSimulation.Digest()))
                            throw new InvalidOperationException("Preview drawing mutated authoritative world state");
                        preview.Checked = true;
                        UnityEngine.Debug.Log("QA:PREDICTION_RENDER_PURE");
                    }
#endif
                }
            }
            catch (Exception ex)
            {
#if COOP_QA
                UnityEngine.Debug.LogError("QA:FAIL Preview rendering: " + ex);
#endif
                Shapez2Multiplayer.logger.Warning?.Log("Preview renderer stopped: " + ex.Message);
                Clear();
            }
        }
    }
    [HarmonyPatch(typeof(HUD), nameof(HUD.OnGameUpdate))]
    internal static class CoopPredictionRenderPatch
    {
        [HarmonyPostfix] private static void Postfix(FrameDrawOptions __1) => CoopPrediction.Draw(__1);
    }
}
