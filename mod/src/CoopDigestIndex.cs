using System;
using System.Collections.Generic;
using System.Linq;
using Shapez2Coop.Protocol;

namespace Shapez2Multiplayer
{
    // Topology only is cached. State/configuration are serialized live after the simulation barrier.
    internal sealed class CoopDigestIndex : IDisposable
    {
        private readonly IMapModel map;
        private readonly List<BuildingModel>[] buildings=new List<BuildingModel>[DigestSchedule.Partitions];
        private readonly List<IslandModel>[] islands=new List<IslandModel>[DigestSchedule.Partitions];
        private readonly BuildingModel[][] sortedBuildings=new BuildingModel[DigestSchedule.Partitions][];
        private readonly IslandModel[][] sortedIslands=new IslandModel[DigestSchedule.Partitions][];
        private int buildingCount,islandCount;
        private bool faulted;
        public bool Owns(IMapModel candidate) => ReferenceEquals(map,candidate);
        public CoopDigestIndex(IMapModel map)
        {
            this.map=map;
            for(int i=0;i<DigestSchedule.Partitions;i++){buildings[i]=new List<BuildingModel>();islands[i]=new List<IslandModel>();}
            foreach(var b in map.Buildings)AddBuilding(b);
            foreach(var i in map.Islands)AddIsland(i);
            map.OnBuildingAdded.Register(AddBuilding);map.OnBeforeBuildingRemoved.Register(RemoveBuilding);
            map.OnIslandAdded.Register(AddIsland);map.OnBeforeIslandRemoved.Register(RemoveIsland);
        }
        private static int Bucket(BuildingModel b) => DigestSchedule.Partition(b.Tile_G.x,b.Tile_G.y,b.Tile_G.z);
        private static int Bucket(IslandModel i) => DigestSchedule.Partition(i.Position.x,i.Position.y,i.Position.z);
        private void AddBuilding(BuildingModel b)
        {try{int p=Bucket(b);buildings[p].Add(b);sortedBuildings[p]=null!;buildingCount++;}catch{faulted=true;throw;}}
        private void AddIsland(IslandModel i)
        {try{int p=Bucket(i);islands[p].Add(i);sortedIslands[p]=null!;islandCount++;}catch{faulted=true;throw;}}
        private void RemoveBuilding(BuildingModel b)
        {try{int p=Bucket(b);if(!buildings[p].Remove(b))throw new InvalidOperationException("Digest building missing");sortedBuildings[p]=null!;buildingCount--;}catch{faulted=true;throw;}}
        private void RemoveIsland(IslandModel i)
        {try{int p=Bucket(i);if(!islands[p].Remove(i))throw new InvalidOperationException("Digest island missing");sortedIslands[p]=null!;islandCount--;}catch{faulted=true;throw;}}
        public void Validate()
        {
            if(faulted || buildingCount!=map.BuildingCount || islandCount!=map.Islands.Count())
                throw new InvalidOperationException("Digest topology index lost agreement");
        }
        public BuildingModel[] Buildings(int p) => sortedBuildings[p]??(sortedBuildings[p]=buildings[p].OrderBy(b=>b.Tile_G.x).ThenBy(b=>b.Tile_G.y).ThenBy(b=>b.Tile_G.z).ToArray());
        public IslandModel[] Islands(int p) => sortedIslands[p]??(sortedIslands[p]=islands[p].OrderBy(i=>i.Position.x).ThenBy(i=>i.Position.y).ThenBy(i=>i.Position.z).ToArray());
        public void Dispose()
        {
            map.OnBuildingAdded.Unregister(AddBuilding);map.OnBeforeBuildingRemoved.Unregister(RemoveBuilding);
            map.OnIslandAdded.Unregister(AddIsland);map.OnBeforeIslandRemoved.Unregister(RemoveIsland);
        }
    }
}
