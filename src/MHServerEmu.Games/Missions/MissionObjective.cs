﻿using Gazillion;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Serialization;
using MHServerEmu.Core.System.Time;
using MHServerEmu.Games.Common;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Missions.Actions;
using MHServerEmu.Games.Missions.Conditions;
using MHServerEmu.Games.Regions;

namespace MHServerEmu.Games.Missions
{
    public enum MissionObjectiveState
    {
        Invalid = 0,
        Available = 1,
        Active = 2,
        Completed = 3,
        Failed = 4,
        Skipped = 5
    }

    [Flags] // Relevant protobuf: NetMessageMissionObjectiveUpdate
    public enum MissionObjectiveUpdateFlags
    {
        None                    = 0,
        State                   = 1 << 0,
        StateExpireTime         = 1 << 1,
        CurrentCount            = 1 << 2,
        FailCurrentCount        = 1 << 3,
        InteractedEntities      = 1 << 4,
        SuppressNotification    = 1 << 5,
        SuspendedState          = 1 << 6,
        Default                 = State | StateExpireTime | CurrentCount | FailCurrentCount | InteractedEntities,
        StateDefault            = State | StateExpireTime | CurrentCount | InteractedEntities,
    }

    public class MissionObjective : ISerialize, IMissionConditionOwner
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private byte _prototypeIndex;

        private MissionObjectiveState _objectiveState;
        private TimeSpan _objectiveStateExpireTime;

        private readonly List<InteractionTag> _interactedEntityList = new();

        private ushort _currentCount;
        private ushort _requiredCount;
        private ushort _failCurrentCount;
        private ushort _failRequiredCount;

        private MissionActionList _onStartActions;
        private MissionActionList _onAvailableActions;
        private MissionActionList _onFailActions;
        private MissionActionList _onSuccessActions;

        private MissionConditionList _successConditions;
        private MissionConditionList _failureConditions;
        private MissionConditionList _activateConditions;

        public Mission Mission { get; }
        public Region Region { get => Mission.Region; }
        public Game Game { get => Mission.Game; }
        public MissionObjectivePrototype Prototype { get; private set; }
        public byte PrototypeIndex { get => _prototypeIndex; }
        public MissionObjectiveState State { get => _objectiveState; }
        public TimeSpan TimeExpire { get => _objectiveStateExpireTime; }
        public TimeSpan TimeRemainingForObjective { get => _objectiveStateExpireTime - Clock.GameTime; }
        public bool IsChangingState { get; private set; }

        public MissionObjective(Mission mission, byte prototypeIndex)
        {
            Mission = mission;
            _prototypeIndex = prototypeIndex;
            Prototype = mission.GetObjectivePrototypeByIndex(prototypeIndex);
        }

        public MissionObjective(byte prototypeIndex, MissionObjectiveState objectiveState, TimeSpan objectiveStateExpireTime,
            IEnumerable<InteractionTag> interactedEntities, ushort currentCount, ushort requiredCount, ushort failCurrentCount, 
            ushort failRequiredCount)
        {
            _prototypeIndex = prototypeIndex;            
            _objectiveState = objectiveState;
            _objectiveStateExpireTime = objectiveStateExpireTime;
            _interactedEntityList.AddRange(interactedEntities);
            _currentCount = currentCount;
            _requiredCount = requiredCount;
            _failCurrentCount = failCurrentCount;
            _failRequiredCount = failRequiredCount;
        }

        public bool Serialize(Archive archive)
        {
            bool success = true;

            success &= Serializer.Transfer(archive, ref _prototypeIndex);

            int state = (int)_objectiveState;
            success &= Serializer.Transfer(archive, ref state);
            _objectiveState = (MissionObjectiveState)state;

            success &= Serializer.Transfer(archive, ref _objectiveStateExpireTime);

            uint numInteractedEntities = (uint)_interactedEntityList.Count;
            success &= Serializer.Transfer(archive, ref numInteractedEntities);

            if (archive.IsPacking)
            {
                foreach (InteractionTag tag in _interactedEntityList)
                {
                    ulong entityId = tag.EntityId;
                    ulong regionId = tag.RegionId;
                    success &= Serializer.Transfer(archive, ref entityId);
                    success &= Serializer.Transfer(archive, ref regionId);
                    // timestamp - ignored in replication
                }
            }
            else
            {
                _interactedEntityList.Clear();

                for (uint i = 0; i < numInteractedEntities; i++)
                {
                    ulong entityId = 0;
                    ulong regionId = 0;
                    success &= Serializer.Transfer(archive, ref entityId);
                    success &= Serializer.Transfer(archive, ref regionId);
                    // timestamp - ignored in replication
                }
            }

            if (archive.IsReplication)
            {
                // Counts are serialized only in replication
                success &= Serializer.Transfer(archive, ref _currentCount);
                success &= Serializer.Transfer(archive, ref _requiredCount);
                success &= Serializer.Transfer(archive, ref _failCurrentCount);
                success &= Serializer.Transfer(archive, ref _failRequiredCount);
            }

            if (archive.IsReplication == false)
                success &= SerializeConditions(archive);

            return success;
        }

        public bool SerializeConditions(Archive archive)
        {
            // TODO MissionConditionList.CreateConditionList
            return true;
        }

        public override string ToString()
        {
            string expireTime = _objectiveStateExpireTime != TimeSpan.Zero ? Clock.GameTimeToDateTime(_objectiveStateExpireTime).ToString() : "0";
            return $"state={_objectiveState}, expireTime={expireTime}, numInteractions={_interactedEntityList.Count}, count={_currentCount}/{_requiredCount}, failCount={_failCurrentCount}/{_failRequiredCount}";
        }

        public bool HasInteractedWithEntity(WorldEntity entity)
        {
            ulong entityId = entity.Id;
            ulong regionId = entity.IsInWorld ? entity.RegionLocation.RegionId : entity.ExitWorldRegionLocation.RegionId;

            if (_interactedEntityList.Count >= 20)
                Logger.Warn($"HasInteractedWithEntity(): MissionObjective {_prototypeIndex} of mission {Mission.GetTraceName()} is tracking more than 20 interacted entities ({_interactedEntityList.Count})");

            foreach (InteractionTag tag in _interactedEntityList)
                if (tag.EntityId == entityId && tag.RegionId == regionId)
                    return true;

            return false;
        }

        public bool GetCompletionCount(ref ushort currentCount, ref ushort requiredCount)
        {
            currentCount = _currentCount;
            requiredCount = _requiredCount;
            return requiredCount > 1;
        }

        public bool GetFailCount(ref ushort currentCount, ref ushort requiredCount)
        {
            currentCount = _failCurrentCount;
            requiredCount = _failRequiredCount;
            return requiredCount > 1;
        }

        public void UpdateState(MissionObjectiveState newState)
        {
            OnUnsetState();
            _objectiveState = newState;
        }

        public bool SetState(MissionObjectiveState newState)
        {
            var oldState = _objectiveState;
            if (oldState == newState) return false;

            if (Mission.IsSuspended)
            {
                _objectiveState = newState;
                return false;
            }

            IsChangingState = true;

            bool success = true;
            success &= OnUnsetState();
            if (success)
            {
                _objectiveState = newState;
                success &= OnSetState(true);
            }
            if (success)
            {
                if (Mission.IsChangingState == false)
                    SendToParticipants(MissionObjectiveUpdateFlags.StateDefault);
                success &= Mission.OnObjectiveStateChange(this);
            }

            IsChangingState = false;

            OnChangeState(); 
            return success;
        }

        private bool OnChangeState()
        {
            if (Mission.IsSuspended) return false;
            return State switch
            {
                MissionObjectiveState.Available => OnChangeStateAvailable(),
                MissionObjectiveState.Active => OnChangeStateActive(),
                _ => false,
            };
        }

        private bool OnChangeStateAvailable()
        {
            if (_activateConditions != null && _activateConditions.IsCompleted)
                return SetState(MissionObjectiveState.Active);
            return false;
        }

        private bool OnChangeStateActive()
        {
            if (_failureConditions != null && _failureConditions.IsCompleted)
                return SetState(MissionObjectiveState.Failed);
            else if (_successConditions != null && _successConditions.IsCompleted)
                return SetState(MissionObjectiveState.Completed);
            return false;
        }

        private bool OnSetState(bool reset = false)
        {
            return _objectiveState switch
            {
                MissionObjectiveState.Invalid | MissionObjectiveState.Skipped => true,
                MissionObjectiveState.Available => OnSetStateAvailable(reset),
                MissionObjectiveState.Active => OnSetStateActive(reset),
                MissionObjectiveState.Completed => OnSetStateCompleted(reset),
                MissionObjectiveState.Failed => OnSetStateFailed(reset),
                _ => false,
            };
        }

        private bool OnUnsetState()
        {
            return _objectiveState switch
            {
                MissionObjectiveState.Invalid | MissionObjectiveState.Skipped => true,
                MissionObjectiveState.Available => OnUnsetStateAvailable(),
                MissionObjectiveState.Active => OnUnsetStateActive(),
                MissionObjectiveState.Completed => OnUnsetStateCompleted(),
                MissionObjectiveState.Failed => OnUnsetStateFailed(),
                _ => true,
            };
        }

        private bool OnSetStateAvailable(bool reset)
        {
            var objetiveProto = Prototype;
            if (objetiveProto == null) return false;
            if (MissionActionList.CreateActionList(ref _onAvailableActions, objetiveProto.OnAvailableActions, Mission, reset) == false
                || MissionConditionList.CreateConditionList(ref _activateConditions, objetiveProto.ActivateConditions, Mission, this, true) == false)
                return false;

            if (reset && _activateConditions != null)
                _activateConditions.Reset();

            return true;
        }

        private bool OnUnsetStateAvailable()
        {
            if (_onAvailableActions != null && _onAvailableActions.Deactivate() == false) return false;
            var region = Region;
            if (region != null)
                _activateConditions?.UnRegisterEvents(region);
            return true;
        }

        private bool OnSetStateActive(bool reset)
        {
            var objetiveProto = Prototype;
            if (objetiveProto == null) return false;

            if (reset)
                _interactedEntityList.Clear();

            // TODO objetiveProto.TimeLimitSeconds

            if (objetiveProto.SuccessConditions != null)
                Mission.RemoteNotificationForConditions(objetiveProto.SuccessConditions);

            if (MissionActionList.CreateActionList(ref _onStartActions, objetiveProto.OnStartActions, Mission, reset) == false
                || MissionConditionList.CreateConditionList(ref _successConditions, objetiveProto.SuccessConditions, Mission, this, true) == false
                || MissionConditionList.CreateConditionList(ref _failureConditions, objetiveProto.FailureConditions, Mission, this, true) == false) 
               return false;

            if (reset)
            {
                if (_successConditions != null)
                {
                    _successConditions.Reset();
                    if (State != MissionObjectiveState.Active) return true;
                }

                if (_failureConditions != null)
                {
                    _failureConditions.Reset();
                    if (State != MissionObjectiveState.Active) return true;
                }

                UpdateCompletionCount();
                UpdateMetaGameWidget();
            }

            return true;
        }

        private bool OnUnsetStateActive()
        {
            var objetiveProto = Prototype;
            if (objetiveProto == null) return false;

            // TODO objetiveProto.ItemDropsCleanupRemaining

            if (_onStartActions != null && _onStartActions.Deactivate() == false) return false;

            _interactedEntityList.Clear();

            // TODO clear objetiveProto.TimeLimitSeconds

            RemoveMetaGameWidget(); 

            var region = Region;
            if (region != null)
            {
                _successConditions?.UnRegisterEvents(region);
                _failureConditions?.UnRegisterEvents(region);
            }

            return true;
        }

        private bool OnSetStateCompleted(bool reset)
        {
            var objetiveProto = Prototype;
            if (objetiveProto == null) return false;
            if (MissionActionList.CreateActionList(ref _onSuccessActions, objetiveProto.OnSuccessActions, Mission, reset) == false)
                return false;

            if (reset)
            {
                // TODO rewards
                // region PlayerCompletedMissionObjectiveGameEvent invoke
                UpdateMetaGameWidget();
            }

            return true;
        }

        private bool OnUnsetStateCompleted()
        {
            return _onSuccessActions == null || _onSuccessActions.Deactivate();
        }

        private bool OnSetStateFailed(bool reset)
        {
            var objetiveProto = Prototype;
            if (objetiveProto == null) return false;
            if (MissionActionList.CreateActionList(ref _onFailActions, objetiveProto.OnFailActions, Mission, reset) == false)
                return false;

            if (reset && (objetiveProto.FailureFailsMission || objetiveProto.Required) && Mission.State != MissionState.Failed) 
                Mission.SetState(MissionState.Failed);

            return true;
        }

        private bool OnUnsetStateFailed()
        {
            return _onFailActions == null || _onFailActions.Deactivate();
        }

        public void ResetConditions()
        {
            switch (State)
            {
                case MissionObjectiveState.Available:

                    _activateConditions?.ResetList();

                    break;

                case MissionObjectiveState.Active:

                    _successConditions?.ResetList();
                    _failureConditions?.ResetList();

                    break;
            }
        }

        public bool OnLoaded()
        {
            UpdateCompletionCount();
            return Mission.IsSuspended || OnSetState();
        }

        private void UpdateCompletionCount()
        {
            // TODO SendToParticipants(MissionObjectiveUpdateFlags.CurrentCount);
            // SendToParticipants(MissionObjectiveUpdateFlags.FailCurrentCount);
            // region MissionObjectiveUpdatedGameEvent
        }

        private void UpdateMetaGameWidget()
        {
            // TODO NetMessageUISyncDataUpdate objetiveProto.MetaGameWidget
        }

        private void RemoveMetaGameWidget()
        {
            // TODO NetMessageUISyncDataRemove objetiveProto.MetaGameWidget
        }

        public void SendToParticipants(MissionObjectiveUpdateFlags objectiveFlags)
        {
            var missionProto = Mission.Prototype;
            if (missionProto == null || missionProto.HasClientInterest == false) return;

            List<Entity> participants = new();
            if (Mission.GetParticipants(participants))
                foreach (var participant in participants)
                    if (participant is Player player)
                        SendUpdateToPlayer(player, objectiveFlags);
        }

        public void SendUpdateToPlayer(Player player, MissionObjectiveUpdateFlags objectiveFlags)
        {
            if (objectiveFlags == MissionObjectiveUpdateFlags.None) return;

            var message = NetMessageMissionObjectiveUpdate.CreateBuilder();
            message.SetMissionPrototypeId((ulong)Mission.PrototypeDataRef);
            message.SetObjectiveIndex(PrototypeIndex);

            if (objectiveFlags.HasFlag(MissionObjectiveUpdateFlags.State))
                message.SetObjectiveState((uint)State);

            if (objectiveFlags.HasFlag(MissionObjectiveUpdateFlags.StateExpireTime))
                message.SetObjectiveStateExpireTime((ulong)TimeExpire.TotalMilliseconds);

            if (objectiveFlags.HasFlag(MissionObjectiveUpdateFlags.CurrentCount))
            {
                message.SetCurrentCount(_currentCount);
                message.SetRequiredCount(_requiredCount);
            }

            if (objectiveFlags.HasFlag(MissionObjectiveUpdateFlags.FailCurrentCount))
            {
                message.SetFailCurrentCount(_failCurrentCount);
                message.SetFailRequiredCount(_failRequiredCount);
            }

            if (objectiveFlags.HasFlag(MissionObjectiveUpdateFlags.InteractedEntities))
            { 
                if (_interactedEntityList.Count == 0)
                {
                    var tagMessage = NetStructMissionInteractionTag.CreateBuilder()
                        .SetEntityId(Entity.InvalidId)
                        .SetRegionId(0).Build();
                    message.AddInteractedEntities(tagMessage);
                }
                else
                {
                    foreach(var tag in _interactedEntityList)
                    {
                        var tagMessage = NetStructMissionInteractionTag.CreateBuilder()
                            .SetEntityId(tag.EntityId)
                            .SetRegionId(tag.RegionId).Build();
                        message.AddInteractedEntities(tagMessage);
                    }
                }
            }

            if (objectiveFlags.HasFlag(MissionObjectiveUpdateFlags.SuppressNotification))
                message.SetSuppressNotification(true);

            if (objectiveFlags.HasFlag(MissionObjectiveUpdateFlags.SuspendedState))
                message.SetSuspendedState(Mission.IsSuspended);

            player.SendMessage(message.Build());
        }
    }
}
