﻿using Gazillion;
using MHServerEmu.Core.Extensions;
using MHServerEmu.Core.Logging;
using MHServerEmu.Games.Entities.Inventories;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.Events.Templates;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Network;
using MHServerEmu.Games.Populations;
using MHServerEmu.Games.Properties;
using MHServerEmu.Games.Regions;

namespace MHServerEmu.Games.Entities
{
    public class Spawner : WorldEntity
    {
        public bool DebugLog;
        private static readonly Logger Logger = LogManager.CreateLogger();

        private EventPointer<SpawnerDefeatEvent> _defeatEvent = new();
        private EventPointer<SpawnIntervalEvent> _spawnEvent = new();

        private Dictionary<ulong, SpawnerSequenceEntryPrototype> _spawnedSequences = new(); 
        private readonly HashSet<int> _uniqueSequences = new ();
        private bool _triggered;
        private int _spawned;
        private bool _defeated;
        private int _sequenceIndex;

        public SpawnerPrototype SpawnerPrototype => Prototype as SpawnerPrototype;
        public bool IsActive { get; private set; }

        public Spawner(Game game) : base(game) { }

        public override void OnExitedWorld()
        {
            base.OnExitedWorld();
            CancelSpawnIntervalEvent();
        }

        public override void OnEnteredWorld(EntitySettings settings)
        {
            base.OnEnteredWorld(settings);
            var spawnerProto = SpawnerPrototype;
            DebugLog = false;
            if (DebugLog) Logger.Debug($"[{Id}] {PrototypeName} [{spawnerProto.StartEnabled}] Distance[{spawnerProto.SpawnDistanceMin}-{spawnerProto.SpawnDistanceMax}] Sequence[{spawnerProto.SpawnSequence.Length}]");

            if (_triggered == false)
            {
                if (spawnerProto.StartEnabled)
                    Trigger(EntityTriggerEnum.Enabled);
                else
                    Trigger(EntityTriggerEnum.Disabled);
            }

            var hotspotRef = spawnerProto.HotspotTrigger;
            if (hotspotRef != PrototypeId.Invalid)
            {
                if (Region == null) return;
                EntitySettings hotspotSettiongs = new()
                {
                    EntityRef = hotspotRef,
                    RegionId = Region.Id,
                    Position = RegionLocation.Position,
                };

                var inventory = GetInventory(InventoryConvenienceLabel.Summoned);
                if (inventory != null) hotspotSettiongs.InventoryLocation = new(Id, inventory.PrototypeDataRef);
                var hotspot = Game.EntityManager.CreateEntity(hotspotSettiongs);
                if (hotspot != null)
                    hotspot.Properties[PropertyEnum.HotspotTriggerEntity, (int)EntityTriggerEnum.Enabled] = Id;
            }
        }

        public void Spawn()
        {
            if (IsInWorld == false) return;
            var spawnerProto = SpawnerPrototype;
            if (SpawnedCount() < spawnerProto.SpawnSimultaneousMax)
                SpawnEntry(NextSequence(spawnerProto.SpawnSequence));
        }

        private int SpawnedCount()
        {
            var manager = Game.EntityManager;
            int spawnedCount = 0;
            foreach (var inventory in GetInventory(InventoryConvenienceLabel.Summoned))
            {
                var summoned = manager.GetEntity<WorldEntity>(inventory.Id);
                if (summoned == null) continue;                
                if (summoned.IsDead || summoned.IsDestroyed || summoned.IsControlledEntity) continue;
                if (summoned is Hotspot && _spawnedSequences.ContainsKey(summoned.Id) == false) continue;
                spawnedCount++;                
            }
            return spawnedCount;
        }

        public bool FilterEntity(SpawnGroupEntityQueryFilterFlags filterFlag, EntityFilterPrototype entityFilter, EntityFilterContext entityFilterContext,
            AlliancePrototype allianceProto)
        {
            var manager = Game.EntityManager;
            foreach (var inventory in GetInventory(InventoryConvenienceLabel.Summoned))
            {
                var summoned = manager.GetEntity<WorldEntity>(inventory.Id);
                if (summoned == null) continue;
                if (filterFlag.HasFlag(SpawnGroupEntityQueryFilterFlags.NotDeadDestroyedControlled)
                    && (summoned.IsDead || summoned.IsDestroyed || summoned.IsControlledEntity)) continue;
                if (entityFilter != null && entityFilter.Evaluate(summoned, entityFilterContext) == false) continue;
                if (SpawnGroup.EntityQueryAllianceCheck(filterFlag, summoned, allianceProto)) return true;
            }
            return false;
        }

        private SpawnerSequenceEntryPrototype NextSequence(SpawnerSequenceEntryPrototype[] sequence)
        {
            if (sequence.IsNullOrEmpty()) return null;

            int size = sequence.Length;
            for (int i = 0; i < size; i++)
            {
                var entry = sequence[_sequenceIndex];
                bool unique = entry.Unique;
                if (unique == false || _uniqueSequences.Contains(_sequenceIndex) == false)
                {
                    if (unique) _uniqueSequences.Add(_sequenceIndex);
                    _sequenceIndex = (_sequenceIndex + 1) % size;
                    return entry;
                }
                _sequenceIndex = (_sequenceIndex + 1) % size;
            }

            return null;
        }

        private void SpawnEntry(SpawnerSequenceEntryPrototype entry)
        {
            if (entry == null) return;
            var popObject = entry.GetPopObject();
            if (popObject == null) return;
            var region = Region;
            if (region == null) return;

            var spawnerProto = SpawnerPrototype;
            PropertyCollection evalProperties = new();
            if (spawnerProto.EvalSpawnProperties != null)
                region.EvalRegionProperties(spawnerProto.EvalSpawnProperties, evalProperties);
            entry.EvaluateSpawnProperties(evalProperties, region, null);

            List<WorldEntity> spawnedEntities = new();
            for (int i = 0; i < entry.Count; i++) 
            { 
                if (DebugLog) Logger.Debug($"SpawnObject[{i}] {popObject.GetType().Name}");
                SpawnObject(popObject, evalProperties, spawnedEntities);
            }
            _spawned += spawnedEntities.Count;

            if (entry.OnKilledDefeatSpawner || entry.OnDefeatAIOverride != PrototypeId.Invalid)
                foreach (var entity in spawnedEntities)
                    _spawnedSequences[entity.Id] = entry;

            if (entry.OnSpawnOverheadTexts.HasValue())
                foreach(var entity in spawnedEntities)
                    foreach(var text in entry.OnSpawnOverheadTexts)
                    {
                        if (text == null || text.OverheadText == LocaleStringId.Blank) continue;
                        if (text.OverheadTextEntityFilter == null || text.OverheadTextEntityFilter.Evaluate(entity, new()))
                            entity.ShowOverheadText(text.OverheadText, 10.0f);
                    }
        }

        private void SpawnObject(PopulationObjectPrototype popObject, PropertyCollection properties, List<WorldEntity> spawnedEntities)
        {
            var populationManager = Region.PopulationManager;
            var spawnerProto = SpawnerPrototype;

            SpawnFlags spawnFlags = SpawnFlags.None;
            if (spawnerProto.SpawnFailBehavior.HasFlag(SpawnFailBehavior.RetryIgnoringBlackout)
                || spawnerProto.SpawnFailBehavior.HasFlag(SpawnFailBehavior.RetryForce))
                spawnFlags |= SpawnFlags.IgnoreBlackout;

            if (spawnerProto.OnDestroyCleanupSpawnedEntities == false)
                properties[PropertyEnum.DetachOnContainerDestroyed] = true;
            if (spawnerProto.SpawnsInheritMissionPrototype)
                properties[PropertyEnum.MissionPrototype] = Properties[PropertyEnum.MissionPrototype];
            if (SpawnGroup != null)
                properties[PropertyEnum.ParentSpawnerGroupId] = SpawnGroup.SpawnerId;

            populationManager.SpawnObject(popObject, RegionLocation, properties, spawnFlags, this, spawnedEntities);
        }

        public bool SpawnLifetimeMaxOut()
        {
            var spawnerProto = SpawnerPrototype;
            return spawnerProto.SpawnLifetimeMax != 0 && _spawned >= spawnerProto.SpawnLifetimeMax;
        }

        public override void Trigger(EntityTriggerEnum trigger)
        {
            if (trigger != EntityTriggerEnum.Disabled && SpawnLifetimeMaxOut()) return;

            base.Trigger(trigger);

            if (trigger != EntityTriggerEnum.Disabled && IsScheduledToDestroy) return;

            switch (trigger)
            {
                case EntityTriggerEnum.Pulse:

                    Spawn();
                    break;

                case EntityTriggerEnum.Enabled:

                    if (_spawnEvent.IsValid) break;                    
                    IsActive = true;
                    Spawn();
                    ScheduleSpawnIntervalEvent();
                    ScheduleDefeatTimeoutEvent();
                    Properties[PropertyEnum.Enabled] = true;                    
                    break;

                case EntityTriggerEnum.Disabled:

                    CancelSpawnIntervalEvent();
                    Properties[PropertyEnum.Enabled] = false;
                    break;
            }

            _triggered = true;
        }

        public void KillSummonedInventory()
        {
            var manager = Game.EntityManager;
            foreach (var inventory in GetInventory(InventoryConvenienceLabel.Summoned))
            {
                var summoned = manager.GetEntity<WorldEntity>(inventory.Id);
                if (summoned != null && summoned.IsDead == false)
                    summoned.Kill();
            }
        }

        private bool DefeatSpawnerOnKilled(WorldEntity entity)
        {
            if (_defeated) return false;

            var spawnerProto = SpawnerPrototype;
            if (spawnerProto.DefeatCriteria == SpawnerDefeatCriteria.MaxReachedAndNoHostileMobs && SpawnLifetimeMaxOut())
            {
                var allianceProto = GameDatabase.GlobalsPrototype.PlayerAlliancePrototype;
                var filterFlags = SpawnGroupEntityQueryFilterFlags.Hostiles | SpawnGroupEntityQueryFilterFlags.NotDeadDestroyedControlled;
                if (FilterEntity(filterFlags, null, new(), allianceProto) == false) return true;
            }

            if (entity != null)
                if (_spawnedSequences.TryGetValue(entity.Id, out var entry))
                {
                    bool defeatSpawner = entry.OnKilledDefeatSpawner;
                    _spawnedSequences.Remove(entity.Id);

                    // check exist entities
                    if (defeatSpawner)
                        foreach(var anyEntry in _spawnedSequences.Values)
                            if (anyEntry.OnKilledDefeatSpawner)
                            {
                                defeatSpawner = false;
                                break;
                            }

                    return defeatSpawner;
                }

            return false;
        }

        public void OnKilledDefeatSpawner(WorldEntity entity, WorldEntity killer)
        {
            if (entity.IsOwnedBy(Id) == false) return;
            if (DefeatSpawnerOnKilled(entity)) SpawnerDefeat(killer);
        }

        private void SpawnerDefeat(WorldEntity killer)
        {
            if (_defeated) return;
            _defeated = true;

            Trigger(EntityTriggerEnum.Disabled);
            Properties[PropertyEnum.IsDead] = true;

            var killMessage = NetMessageEntityKill.CreateBuilder()
                .SetIdEntity(Id)
                .SetIdKillerEntity(killer != null ? killer.Id : InvalidId)
                .SetKillFlags(0).Build();

            Game.NetworkManager.SendMessageToInterested(killMessage, this, AOINetworkPolicyValues.AOIChannelProximity);

            var spec = SpawnSpec;
            if (spec != null && spec.State == SpawnState.Defeated || spec.State == SpawnState.Destroyed) return;

            var manager = Game.EntityManager;
            var spawnerProto = SpawnerPrototype;
            if (spawnerProto.OnDefeatBannerMessage != null)
                foreach (ulong playerId in InterestReferences.PlayerIds)
                {
                    var player = manager.GetEntity<Player>(playerId); 
                    player?.SendBannerMessage(spawnerProto.OnDefeatBannerMessage);
                }

            foreach(var kvp in _spawnedSequences)
            {
                var aiOverrideRef = kvp.Value.OnDefeatAIOverride;
                if (aiOverrideRef != PrototypeId.Invalid)
                {
                    var agent = manager.GetEntity<Agent>(kvp.Key);
                    if (agent?.AIController != null)
                        agent.AIController.Blackboard.PropertyCollection[PropertyEnum.AIFullOverride] = aiOverrideRef;
                }
            }

            spec?.Defeat(killer);

            if (killer != null)
            {
                var player = killer.GetSelfOrOwnerOfType<Player>();
                if (player != null)
                    Region?.SpawnerDefeatedEvent.Invoke(new(player, this));
            } 
        }

        private void SpawnInterval()
        {
            if (SpawnLifetimeMaxOut() == false)
            {
                Spawn();
                ScheduleSpawnIntervalEvent();
            }
            else
            {
                if (DefeatSpawnerOnKilled(null))
                    SpawnerDefeat(null);
            }
        }

        private void ScheduleDefeatTimeoutEvent()
        {
            if (_defeated) return;

            var spawnerProto = SpawnerPrototype;
            if (spawnerProto.DefeatTimeoutMS > 0)
            {
                var scheduler = Game.GameEventScheduler;
                if (scheduler == null) return;
                var timeOffset = TimeSpan.FromMilliseconds(spawnerProto.DefeatTimeoutMS);
                if (_defeatEvent.IsValid)
                    scheduler.RescheduleEvent(_defeatEvent, timeOffset);
                else
                    ScheduleEntityEvent(_defeatEvent, timeOffset);
            }
        }

        private void ScheduleSpawnIntervalEvent()
        {
            if (_spawnEvent.IsValid) return;

            var spawnerProto = SpawnerPrototype;
            int spawnInterval = spawnerProto.SpawnIntervalMS;

            if (spawnerProto.SpawnIntervalVarianceMS > 0)
                spawnInterval += Game.Random.Next(0, spawnerProto.SpawnIntervalVarianceMS);

            spawnInterval = Math.Max(500, spawnInterval);

            var scheduler = Game.GameEventScheduler;
            if (scheduler == null) return;
            var timeOffset = TimeSpan.FromMilliseconds(spawnInterval);
            ScheduleEntityEvent(_spawnEvent, timeOffset);
        }

        private void CancelSpawnIntervalEvent()
        {
            Game?.GameEventScheduler?.CancelEvent(_spawnEvent);
        }

        protected class SpawnerDefeatEvent : CallMethodEvent<Entity>
        {
            protected override CallbackDelegate GetCallback() => (t) => (t as Spawner)?.SpawnerDefeat(null);
        }

        protected class SpawnIntervalEvent : CallMethodEvent<Entity>
        {
            protected override CallbackDelegate GetCallback() => (t) => (t as Spawner)?.SpawnInterval();
        }
    }
}
