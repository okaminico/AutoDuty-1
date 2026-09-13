using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameFunctions;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using System;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ECommons.Throttlers;
using AutoDuty.IPC;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace AutoDuty.Helpers
{
    using Lumina.Excel.Sheets;

    internal static class ObjectHelper
    {
        // 某些內容（例如極火龍殲滅戰打完後的剝取素材）同一個 DataId 會同時存在好幾份、
        // 座標完全相同的個體物件（每個玩家各自一份自己專屬的可互動物件），距離排序完全排不出
        // 差異、等於隨機挑。挑到別人那份就會一直 IsTargetable == false，互動永遠卡住。
        // 優先挑「當下真的可互動」的那份，挑不到才照舊退回最近的（給尚未刷出／尚未可互動時的
        // 移動目標用）。
        internal static bool TryGetObjectByDataId(uint dataId, out IGameObject? gameObject) => (gameObject = Svc.Objects.Where(x => x.BaseId == dataId).OrderByDescending(x => x.IsTargetable).ThenBy(GetDistanceToPlayer).FirstOrDefault()) != null;

        // ⚠️ 不要把 IGameObject 捕獲進 TaskManager 的閉包跨幀用。
        // Dalamud 的 GameObject.Address 在建構時就凍結、永不重新解析
        // (GameObject.cs:137-139),而 IGameObject.IsValid() 只檢查「玩家有沒有登入」、
        // 完全不驗證位址(GameObject.cs:170-177)。所以存 IGameObject == 存一根原生指標,
        // 而排隊中的任務是在「後面的幀」才執行、甚至會反覆重跑很多幀。
        // 正解:閉包只捕獲 GameObjectId,每次執行時用 ResolveObject 重查物件表,
        // 查不到就中止該行為(fail-closed)。
        internal static bool TryGetObjectIdByDataId(uint dataId, out ulong? objectId)
        {
            objectId = Svc.Objects.Where(x => x.BaseId == dataId).OrderByDescending(x => x.IsTargetable).ThenBy(GetDistanceToPlayer).FirstOrDefault()?.GameObjectId;
            return objectId != null;
        }

        internal static ulong? GetObjectIdByDataId(uint id) => Svc.Objects.Where(o => o.BaseId == id).OrderByDescending(o => o.IsTargetable).ThenBy(GetDistanceToPlayer).FirstOrDefault()?.GameObjectId;

        internal static IGameObject? ResolveObject(ulong? objectId) => objectId is null ? null : Svc.Objects.SearchById(objectId.Value);

        internal static List<IGameObject>? GetObjectsByObjectKind(ObjectKind objectKind) => [.. Svc.Objects.OrderBy(GetDistanceToPlayer).Where(o => o.ObjectKind == objectKind)];

        internal static IGameObject? GetObjectByObjectKind(ObjectKind objectKind) => Svc.Objects.OrderBy(GetDistanceToPlayer).FirstOrDefault(o => o.ObjectKind == objectKind);

        internal static List<IGameObject>? GetObjectsByRadius(float radius) => [.. Svc.Objects.OrderBy(GetDistanceToPlayer).Where(o => GetDistanceToPlayer(o) <= radius)];

        internal static IGameObject? GetObjectByRadius(float radius) => Svc.Objects.OrderBy(GetDistanceToPlayer).FirstOrDefault(o => GetDistanceToPlayer(o) <= radius);

        internal static List<IGameObject>? GetObjectsByName(string name) => [.. Svc.Objects.OrderBy(GetDistanceToPlayer).Where(o => o.Name.TextValue.Equals(name, StringComparison.CurrentCultureIgnoreCase))];

        internal static IGameObject? GetObjectByName(string name) => Svc.Objects.OrderBy(GetDistanceToPlayer).FirstOrDefault(o => o.Name.TextValue.Equals(name, StringComparison.CurrentCultureIgnoreCase));

        internal static IGameObject? GetObjectByDataId(uint id) => Svc.Objects.OrderBy(GetDistanceToPlayer).FirstOrDefault(o => o.BaseId == id);

        internal static List<IGameObject>? GetObjectsByPartialName(string name) => [.. Svc.Objects.OrderBy(GetDistanceToPlayer).Where(o => o.Name.TextValue.Contains(name, StringComparison.CurrentCultureIgnoreCase))];

        internal static IGameObject? GetObjectByPartialName(string name) => Svc.Objects.OrderBy(GetDistanceToPlayer).FirstOrDefault(o => o.Name.TextValue.Contains(name, StringComparison.CurrentCultureIgnoreCase));

        internal static List<IGameObject>? GetObjectsByNameAndRadius(string objectName) => [.. Svc.Objects.OrderBy(GetDistanceToPlayer).Where(g => g.Name.TextValue.Equals(objectName, StringComparison.CurrentCultureIgnoreCase) && Vector3.Distance(Player.Object.Position, g.Position) <= 10)];

        internal static IGameObject? GetObjectByNameAndRadius(string objectName) => Svc.Objects.OrderBy(GetDistanceToPlayer).FirstOrDefault(g => g.Name.TextValue.Equals(objectName, StringComparison.CurrentCultureIgnoreCase) && Vector3.Distance(Player.Object.Position, g.Position) <= 10);

        internal static IBattleChara? GetBossObject(int radius = 100) => GetObjectsByRadius(radius)?.OfType<IBattleChara>().FirstOrDefault(b => IsBossFromIcon(b) || BossMod_IPCSubscriber.HasModuleByDataId(b.BaseId));

        internal static unsafe float GetDistanceToPlayer(IGameObject gameObject) => GetDistanceToPlayer(gameObject.Position);

        internal static unsafe float GetDistanceToPlayer(Vector3 v3) => Vector3.Distance(v3, Player.GameObject->Position);

        internal static unsafe bool BelowDistanceToPlayer(Vector3 v3, float maxDistance, float maxHeightDistance) => BelowDistanceToPoint(v3, Player.GameObject->Position, maxDistance, maxHeightDistance);

        /// <summary>與 <see cref="BelowDistanceToPlayer"/> 同樣的圓柱體判定,但原點可以是任意座標(例如路徑步驟自己的位置)。</summary>
        internal static bool BelowDistanceToPoint(Vector3 target, Vector3 origin, float maxDistance, float maxHeightDistance) => Vector3.Distance(target, origin) < maxDistance &&
                                                                                                                                 MathF.Abs(target.Y - origin.Y) < maxHeightDistance;

        internal static unsafe IGameObject? GetPartyMemberFromRole(string role)
        {
            if (Player.Object != null && Player.Object.ClassJob.Value.GetJobRole().ToString().Contains(role, StringComparison.InvariantCultureIgnoreCase))
            {
                return Player.Object;
            }

            if (Svc.Party.PartyId != 0)
            {
                return Svc.Party.FirstOrDefault(x => x.ClassJob.Value.GetJobRole().ToString().Contains(role, StringComparison.InvariantCultureIgnoreCase))?.GameObject;
            }

            var buddies = UIState.Instance()->Buddy.BattleBuddies.ToArray().Where(x => x.DataId != 0);
            foreach (var buddy in buddies)
            {
                var gameObject = Svc.Objects.FirstOrDefault(x => x.EntityId == buddy.EntityId);

                if (gameObject == null) 
                    continue;

                var classJob = ((ICharacter)gameObject).ClassJob.ValueNullable;

                if (classJob == null) 
                    continue;

                if (classJob.Value.GetJobRole().ToString().Contains(role, StringComparison.InvariantCultureIgnoreCase))
                    return gameObject;
            }
            return null;
        }

        internal static unsafe IGameObject? GetTankPartyMember() => GetPartyMemberFromRole("Tank");

        internal static unsafe IGameObject? GetHealerPartyMember() => GetPartyMemberFromRole("Healer");

        //RotationSolver
        internal static unsafe float GetBattleDistanceToPlayer(IGameObject gameObject)
        {
            if (gameObject == null) return float.MaxValue;
            var player = Player.Object;
            if (player == null) return float.MaxValue;

            var distance = Vector3.Distance(player.Position, gameObject.Position) - player.HitboxRadius;
            distance -= gameObject.HitboxRadius;
            return distance;
        }

        internal static BNpcBase? GetObjectNPC(IGameObject gameObject) => Svc.Data.GetExcelSheet<BNpcBase>()?.GetRow(gameObject.BaseId) ?? null;

        //From RotationSolver
        internal static bool IsBossFromIcon(IGameObject gameObject) => GetObjectNPC(gameObject)?.Rank is 1 or 2 or 6;

        internal static unsafe void InteractWithObject(IGameObject? gameObject, bool face = true)
        {
            try
            {
                if (gameObject == null || !gameObject.IsTargetable) 
                    return;
                if (face) 
                    Plugin.OverrideCamera.Face(gameObject.Position);
                var gameObjectPointer = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)gameObject.Address;
                TargetSystem.Instance()->InteractWithObject(gameObjectPointer, false);
            }
            catch (Exception ex)
            {
                Svc.Log.Info($"InteractWithObject: Exception: {ex}");
            }
        }
        internal static unsafe AtkUnitBase* InteractWithObjectUntilAddon(IGameObject? gameObject, string addonName)
        {
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(addonName, out var addon) && GenericHelpers.IsAddonReady(addon))
                return addon;

            if (EzThrottler.Throttle("InteractWithObjectUntilAddon"))
                InteractWithObject(gameObject);
            
            return null;
        }

        internal static unsafe bool InteractWithObjectUntilNotValid(IGameObject? gameObject)
        {
            if (gameObject == null || !PlayerHelper.IsValid)
                return true;

            if (EzThrottler.Throttle("InteractWithObjectUntilNotValid"))
                InteractWithObject(gameObject);
            
            return false;
        }

        internal static unsafe bool InteractWithObjectUntilNotTargetable(IGameObject? gameObject)
        {
            if (gameObject == null || !gameObject.IsTargetable)
                return true;

            if (EzThrottler.Throttle("InteractWithObjectUntilNotTargetable"))
                InteractWithObject(gameObject);

            return false;
        }

        internal static bool PartyValidation()
        {
            if (Svc.Party.Count < 4)
                return false;

            var healer = false;
            var tank = false;
            var dpsCount = 0;

            foreach (var item in Svc.Party)
            {
                switch (item.ClassJob.ValueNullable?.Role)
                {
                    case 1:
                        tank = true;
                        break;
                    case 2:
                    case 3:
                        dpsCount++;
                        break;
                    case 4:
                        healer = true;
                        break;
                    default:
                        break;
                }
            }
            return (tank && healer && dpsCount > 1);
        }
    }
}
