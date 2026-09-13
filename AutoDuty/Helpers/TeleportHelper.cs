using FFXIVClientStructs.FFXIV.Client.Game;
using ECommons;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Collections.Generic;
using ECommons.UIHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.Throttlers;
using ECommons.DalamudServices;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;

namespace AutoDuty.Helpers
{
    internal unsafe static class TeleportHelper
    {
        internal static bool TeleportFCEstate() => TeleportHousing(FCEstateTeleportId, 0);

        internal static bool TeleportPersonalHome() => TeleportHousing(PersonalHomeTeleportId, 0);

        internal static bool TeleportApartment() => TeleportHousing(ApartmentTeleportId, 128);

        private static bool TeleportHousing(uint id, byte sub)
        {
            if (id != 0)
            {
                Svc.Log.Debug($"Teleporting to AetheryteId: {id} SubIndex: {sub}");
                return TeleportAetheryte(id, sub);
            }
            else
            {
                Svc.Log.Info("Unable to teleport to specified housing");
                return false;
            }
        }

        // AgentHUD.Instance() 是產生器產出的取得子
        // (`agentModule == null ? null : (AgentHUD*)agentModule->GetAgentByInternalId(AgentId.Hud)`),
        // UIModule/代理人尚未建立時會回 null,底下三個地圖標記屬性原本都無條件解參考。
        // 取不到就回 default:消費端的 *WardCenterVector3 本來就拿 Vector3.Zero 當「沒有這個標記」,
        // 與 FirstOrDefault 找不到時的結果完全一致,不需要再多一條處理路徑。
        private static MapMarkerData FindHousingMapMarker(uint[] iconIds)
        {
            AgentHUD* agentHud = AgentHUD.Instance();
            return agentHud == null ? default : agentHud->MapMarkers.ToList().FirstOrDefault(x => x.IconId.EqualsAny(iconIds));
        }

        internal static MapMarkerData FCEstateMapMarkerData => FindHousingMapMarker((uint[])Enum.GetValuesAsUnderlyingType<FCHousingMarker>());

        internal static Vector3 FCEstateWardCenterVector3 => new(FCEstateMapMarkerData.Position.X, FCEstateMapMarkerData.Position.Y, FCEstateMapMarkerData.Position.Z);

        internal static uint FCEstateTeleportId => Svc.AetheryteList.FirstOrDefault(x => x is { IsApartment: false, IsSharedHouse: false } && x.AetheryteId.EqualsAny<uint>(56, 57, 58, 96, 164))?.AetheryteId ?? 0;

        internal static IGameObject? FCEstateEntranceGameObject => FCEstateWardCenterVector3 != Vector3.Zero ? ObjectHelper.GetObjectsByObjectKind(ObjectKind.EventObj)?.OrderBy(x => Vector3.Distance(x.Position, FCEstateWardCenterVector3)).FirstOrDefault(x => x.BaseId == 2002737) : null;

        internal static MapMarkerData PersonalHomeMapMarkerData => FindHousingMapMarker((uint[])Enum.GetValuesAsUnderlyingType<PrivateHousingMarker>());

        internal static Vector3 PersonalHomeWardCenterVector3 => new(PersonalHomeMapMarkerData.Position.X, PersonalHomeMapMarkerData.Position.Y, PersonalHomeMapMarkerData.Position.Z);

        internal static uint PersonalHomeTeleportId => Svc.AetheryteList.FirstOrDefault(x => x is { IsApartment: false, IsSharedHouse: false } && x.AetheryteId.EqualsAny<uint>(59, 60, 61, 97, 165))?.AetheryteId ?? 0;

        internal static IGameObject? PersonalHomeEntranceGameObject => PersonalHomeWardCenterVector3 != Vector3.Zero ? ObjectHelper.GetObjectsByObjectKind(ObjectKind.EventObj)?.OrderBy(x => Vector3.Distance(x.Position, PersonalHomeWardCenterVector3)).FirstOrDefault(x => x.BaseId == 2002737) : null;

        internal static MapMarkerData ApartmentMapMarkerData => FindHousingMapMarker((uint[])Enum.GetValuesAsUnderlyingType<ApartmentHousingMarker>());

        internal static Vector3 ApartmentWardCenterVector3 => new(ApartmentMapMarkerData.Position.X, ApartmentMapMarkerData.Position.Y, ApartmentMapMarkerData.Position.Z);

        internal static uint ApartmentTeleportId => Svc.AetheryteList.FirstOrDefault(x => x is { IsApartment: true, IsSharedHouse: false } && x.AetheryteId.EqualsAny<uint>(59, 60, 61, 97, 165))?.AetheryteId ?? 0;

        internal static IGameObject? ApartmentEntranceGameObject => ApartmentWardCenterVector3 != Vector3.Zero ? ObjectHelper.GetObjectsByObjectKind(ObjectKind.EventObj)?.OrderBy(x => Vector3.Distance(x.Position, ApartmentWardCenterVector3)).FirstOrDefault(x => x.BaseId == 2007402) : null;

        internal static bool TeleportGCCity()
        {
            //Limsa=1,128, Gridania=2,132, Uldah=3,130 -- Goto Limsa if no GC
            return UIState.Instance()->PlayerState.GrandCompany switch
            {
                1 => TeleportAetheryte(8, 0),
                2 => TeleportAetheryte(2, 0),
                3 => TeleportAetheryte(9, 0),
                _ => TeleportAetheryte(8, 0),
            };
        }

        internal static bool TeleportAetheryte(uint aetheryteId, byte subindex)
        {
            if (PlayerHelper.IsCasting || aetheryteId == 0)
                return true;

            if (!PlayerHelper.IsCasting && EzThrottler.Throttle("TeleportAetheryte", 250))
                TeleportAction(aetheryteId, subindex);

            return false;
        }

        internal static bool MoveToClosestAetheryte()
        {
            IGameObject? gameObject;
            if ((gameObject = ObjectHelper.GetObjectByObjectKind(ObjectKind.Aetheryte)) == null)
                return false;

            return MovementHelper.Move(gameObject, 0.25f, 7f);
        }

        internal static bool TeleportAethernet(string aethernetName, uint toTerritoryType)
        {
            if (aethernetName.IsNullOrEmpty() || !PlayerHelper.IsValid)
                return true;

            if (!GenericHelpers.TryGetAddonByName("TelepotTown", out AtkUnitBase* addon) || !GenericHelpers.IsAddonReady(addon))
            {
                IGameObject? gameObject;
                if ((gameObject = ObjectHelper.GetObjectByObjectKind(ObjectKind.Aetheryte)) == null)
                    return false;

                if ((addon = ObjectHelper.InteractWithObjectUntilAddon(gameObject, "SelectString")) == null)
                    return false;

                // 改走 AddonHelper 才吃得到 AddonPressGuard(原本是裸 Callback.Fire)。
                AddonHelper.FireCallBack(addon, true, 0);

                // 🔴 這裡一定要收手:上面那一發是對 **SelectString** 送的,它會關掉選單並開出
                //    TelepotTown;下面那一發 (true, 11, …) 是要給 TelepotTown 的,原本卻會在
                //    同一次呼叫裡對著<b>正在關閉的 SelectString</b> 送出去(EzThrottler 第一次必定放行,
                //    擋不住),而且 GetAethernetCallback 此時取不到 TelepotTown、只會回 0。
                //    下一次進來時上面的 TelepotTown 分支就會接手。
                //    ⚠️ 兩個呼叫端(GotoHelper)本來就忽略回傳值、每個 tick 重跑,語意不變。
                return false;
            }

            if (EzThrottler.Throttle("TeleportAethernet", 250))
            {
                // 目的地是靠讀 TelepotTown 的文字比對出來的;讀到 U+FFFD 就是窗記憶體變動中,這一輪不送。
                uint callback = GetAethernetCallback(aethernetName, out bool textUnreadable);
                if (!textUnreadable)
                    AddonHelper.FireCallBack(addon, true, 11, callback);
            }

            return false;
        }

        internal static bool TeleportAction(uint aetheryteId, byte subindex = 0)
        {
            ActionManager.Instance()->GetActionStatus(ActionType.Action, 5);

            return Telepo.Instance()->Teleport(aetheryteId, subindex);
        }

        /// <param name="textUnreadable">
        /// 任何一筆目的地名稱含 U+FFFD ⇒ <see langword="true"/>:窗記憶體正在變動,呼叫端這一輪不要送 callback。
        /// 找不到時照舊回 0(這一點沒改)。
        /// </param>
        internal static uint GetAethernetCallback(string aethernetName, out bool textUnreadable)
        {
            textUnreadable = false;

            if (GenericHelpers.TryGetAddonByName("TelepotTown", out AtkUnitBase* addon) && GenericHelpers.IsAddonReady(addon))
            {
                var readerTelepotTown = new ReaderTelepotTown(addon);
                for (int i = 0; i < readerTelepotTown.DestinationData.Count; i++)
                {
                    string name = readerTelepotTown.DestinationName[i].Name;
                    if (AddonPressGuard.IsTextCorrupt("TelepotTown", name))
                    {
                        textUnreadable = true;
                        return 0;
                    }

                    if (aethernetName == name)
                        return readerTelepotTown.DestinationData[i].CallbackData;
                }
            }
            return 0;
        }
    }
    //From Lifestream
    internal unsafe class ReaderTelepotTown(AtkUnitBase* UnitBase, int BeginOffset = 0) : AtkReader(UnitBase, BeginOffset)
    {
        internal uint        NumEntries         => ReadUInt(0) ?? 0;
        internal uint        CurrentDestination => ReadUInt(1) ?? 0;
        internal List<Data>  DestinationData    => Loop<Data>(6, 4, 20);
        internal List<Names> DestinationName    => Loop<Names>(262, 1, 20);

        internal unsafe class Names(nint UnitBasePtr, int BeginOffset = 0) : AtkReader(UnitBasePtr, BeginOffset)
        {
            /// <remarks>
            /// 🔴 這裡<b>不能</b>用 ECommons 的 <c>GetText()</c>:另一端是 Lumina
            /// (<c>GotoHelper</c> 傳進來的是 <c>AethernetName…Name.ToString()</c>),
            /// 而 <c>GetText()</c> 會把地名裡的連字符 payload(<c>02 1F 01 03</c>)整個丟掉
            /// ⇒ 「烏爾達哈 - 納爾階」這類名字<b>永遠比不中</b>,失敗形式是靜默回 0。
            /// </remarks>
            internal string Name => SeStringTextExtractor.ExtractLuminaText(ReadSeString(0));
        }

        internal unsafe class Data(nint UnitBasePtr, int BeginOffset = 0) : AtkReader(UnitBasePtr, BeginOffset)
        {
            internal uint Type         => ReadUInt(0).Value;
            internal uint State        => ReadUInt(1).Value;
            internal uint IconID       => ReadUInt(2).Value;
            internal uint CallbackData => ReadUInt(3).Value;
        }
    }
}
