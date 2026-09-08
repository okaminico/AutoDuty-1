using AutoDuty.IPC;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;

namespace AutoDuty.Helpers
{
    using System.Numerics;
    using global::AutoDuty.Multibox;

    internal static class DeathHelper
    {
        private static PlayerLifeState _deathState = PlayerLifeState.Alive;
        internal static PlayerLifeState DeathState
        {
            get => _deathState;
            set
            {
                // 🔴 這行必須在下面那個 DutyMode 早退之前:MultiboxUtility.Set() 會把
                // DutyModeEnum 設成 Regular,若擺在早退之後,多開模式下(非 Unsynced)
                // 死亡同步永遠不會送出。內部自帶 Config.MultiBox 閘門,沒開就是 no-op。
                if (_deathState != value)
                    MultiboxUtility.IsDead(value == PlayerLifeState.Dead);

                if (Plugin.Configuration.DutyModeEnum.EqualsAny(DutyMode.Regular, DutyMode.Trial, DutyMode.Raid) && !Plugin.Configuration.Unsynced)
                    return;
                else if (value == PlayerLifeState.Dead)
                {
                    if (value != _deathState)
                    {
                        Svc.Log.Debug("DeathHelper - Player is Dead changing state to Dead");
                        SchedulerHelper.ScheduleAction("OnDeath", OnDeath, 500, false); 
                    }
                }
                else if (value == PlayerLifeState.Revived)
                {
                    SchedulerHelper.DescheduleAction("OnDeath");
                    Svc.Log.Debug("DeathHelper - Player is Revived changing state to Revived");
                    _oldIndex = Plugin.Indexer;
                    _findShortcutStartTime = Environment.TickCount;
                    FindShortcut();
                }
                _deathState = value;
            }
        }

        private static unsafe void OnDeath()
        {
            if (Plugin.Configuration.DutyModeEnum.EqualsAny(DutyMode.Regular, DutyMode.Trial, DutyMode.Raid) && !Plugin.Configuration.Unsynced)
                return;

            Plugin.StopForCombat = true;
            Plugin.SkipTreasureCoffer = true;

            if (VNavmesh_IPCSubscriber.Path_IsRunning())
                VNavmesh_IPCSubscriber.Path_Stop();

            if (Plugin.TaskManager.IsBusy)
                Plugin.TaskManager.Abort();
            
            if (Plugin.Configuration.DutyModeEnum.EqualsAny(DutyMode.Regular, DutyMode.Trial, DutyMode.Raid, DutyMode.Variant))
            {
                Svc.Log.Debug("DeathHelper - On Death, looking for YesNo");
                if (GenericHelpers.TryGetAddonByName("SelectYesno", out AtkUnitBase* addonSelectYesno) && GenericHelpers.IsAddonReady(addonSelectYesno))
                    AddonHelper.ClickSelectYesno();
            }
        }

        private static int _oldIndex = 0;
        private static IGameObject? _gameObject => ObjectHelper.GetObjectByDataId(2000700);
        private static int _findShortcutStartTime = 0;

        private static int FindWaypoint()
        {
            /*
            if (Plugin.Indexer == 0)
            {
                float closestWaypointDistance = float.MaxValue;
                int closestWaypointIndex = -1;

                for (int i = 0; i < Plugin.Actions.Count; i++)
                {
                    string node = Plugin.Actions[i].Name;
                    Vector3 position = Plugin.Actions[i].Position;

                    float currentDistance = ObjectHelper.GetDistanceToPlayer(position);
                    if (currentDistance < closestWaypointDistance)

                    {
                        closestWaypointDistance = node.Equals("Boss", StringComparison.InvariantCultureIgnoreCase) ?
                                                      currentDistance :
                                                      ObjectHelper.GetDistanceToPlayer(Plugin.Actions[Plugin.Indexer].Position);
                        closestWaypointIndex = i;
                    }
                }
                Svc.Log.Info($"Closest Waypoint was {closestWaypointIndex}");
                return closestWaypointIndex;
            }*/

            if (Plugin.Indexer != -1)
            {
                // 防禦性寫法,理由跟 ContentPathContainerExtensions 那邊的空容器問題同一類:
                // 死亡重生的當下,CurrentTerritoryType 可能不在 DictionaryPaths 裡(例如這個
                // 副本本來就沒有路徑資料),或 CurrentPath 指到一個已經不存在/被移除的路徑
                // (index 越界或是 -1)。原本的寫法是直接用索引子連續兩層存取,兩種情況都會讓
                // 例外一路逃出 OnRevive,導致 Stop() 永遠執行不到、Framework.Update 訂閱解不掉,
                // 每一幀都重丟例外洗版。查不到就當作「沒有重生點資料」處理,不要整個掛掉。
                bool revivalFound = false;
                if (ContentPathsManager.DictionaryPaths.TryGetValue(Plugin.CurrentTerritoryType, out ContentPathsManager.ContentPathContainer? container)
                    && Plugin.CurrentPath >= 0 && Plugin.CurrentPath < container.Paths.Count)
                {
                    revivalFound = container.Paths[Plugin.CurrentPath].RevivalFound;
                }
                else
                {
                    Svc.Log.Warning($"DeathHelper - Couldn't look up RevivalFound for TerritoryType {Plugin.CurrentTerritoryType}, path index {Plugin.CurrentPath} - assuming no revival point.");
                }

                bool isBoss = Plugin.Actions[Plugin.Indexer].Name.Equals("Boss");
                if (!revivalFound)
                {
                    if (Plugin.Indexer > 0 && isBoss)
                        return Plugin.Indexer;
                }




                Svc.Log.Info($"Finding Revival Point starting at {Plugin.Indexer}. Using Revival Action: {revivalFound}");
                for (int i = Plugin.Indexer; i >= 0; i--)
                {
                    if (Plugin.Actions[i].Name.Equals(isBoss ? "Revival" : "Boss", StringComparison.InvariantCultureIgnoreCase) && i != Plugin.Indexer)
                        return isBoss ? i : i + 1;
                    /* Pre 7.2
                    else
                    {
                        if (Plugin.Actions[i].Name.Equals("Boss", StringComparison.InvariantCultureIgnoreCase) && i != Plugin.Indexer)
                            return i + 1;
                    }*/
                }
            }

            return 0;
        }

        private static void FindShortcut()
        {
            if (_gameObject == null && Environment.TickCount <= (_findShortcutStartTime + 5000))
            {
                Svc.Log.Debug($"OnRevive: Searching for shortcut");
                SchedulerHelper.ScheduleAction("FindShortcut", FindShortcut, 500);
                return;
            }
            
            if (_gameObject == null || !_gameObject.IsTargetable)
            {
                Svc.Log.Debug($"OnRevive: Couldn't find shortcut");
                Plugin.Indexer = 0;
                //Stop();
                //return;
            } else
                Svc.Log.Debug("OnRevive: Found shortcut");
            Svc.Framework.Update += OnRevive;
        }

        internal static void Stop()
        {
            Svc.Framework.Update -= OnRevive;
            if (VNavmesh_IPCSubscriber.Path_IsRunning())
                VNavmesh_IPCSubscriber.Path_Stop();
            Plugin.Stage = Stage.Reading_Path;
            Svc.Log.Debug("DeathHelper - Player is Alive, and we are done with Revived Actions, changing state to Alive");
            _deathState               = PlayerLifeState.Alive;
            Plugin.SkipTreasureCoffer = false;
        }

        private static unsafe void OnRevive(IFramework _)
        {
            if (!EzThrottler.Throttle("OnRevive", 500) || (!PlayerHelper.IsReady && !Conditions.Instance()->OccupiedInQuestEvent) || PlayerHelper.IsCasting) return;

            float distanceToPlayer;

            if (_gameObject == null || !_gameObject.IsTargetable || (distanceToPlayer = ObjectHelper.GetDistanceToPlayer(_gameObject)) > 50)
            {
                Svc.Log.Debug("OnRevive: Done");
                if(Plugin.Indexer == 0) 
                    Plugin.Indexer = FindWaypoint();
                Stop();
                return;
            }
            if (_oldIndex == Plugin.Indexer)
                Plugin.Indexer = FindWaypoint();

            if (distanceToPlayer > 2)
            {
                MovementHelper.Move(_gameObject, 0.25f, 2);
                Svc.Log.Debug($"OnRevive: Moving to {_gameObject.Name} at: {_gameObject.Position} which is {distanceToPlayer} away");
            }
            else
            {
                Svc.Log.Debug($"OnRevive: Interacting with {_gameObject.Name} until SelectYesno Addon appears, and ClickingYes");
                ObjectHelper.InteractWithObjectUntilAddon(_gameObject, "SelectYesno");
                AddonHelper.ClickSelectYesno();
            }
        }
    }
}
