using Dalamud.Memory;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Collections.Generic;
using System.Linq;

namespace AutoDuty.Helpers
{
    using System;
    using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;
    using global::AutoDuty.Multibox;
    using static Data.Classes;

    internal unsafe class QueueHelper : ActiveHelperBase<QueueHelper>
    {
        /// <summary>
        /// 只等待並接受副本確認視窗,不自己排隊。
        /// 📌 供 Multibox 用戶端使用:排隊是主機端做的,用戶端只負責接下彈出的確認。
        /// </summary>
        internal static void InvokeAcceptOnly()
        {
            _dutyMode = DutyMode.None;
            Svc.Log.Information("Queueing: Accepting only");
            Instance.Start();
            Plugin.Action = "Queueing: Waiting to accept";
        }

        internal static void Invoke(Content? content, DutyMode dutyMode)
        {
            if (State != ActionState.Running && content != null && dutyMode != DutyMode.None)
            {
                _dutyMode = dutyMode;
                _content = content;
                Svc.Log.Info($"Queueing: {dutyMode}: {content.Name}");

                Instance.Start();
                Plugin.Action = $"Queueing {_dutyMode}: {content.Name}";
            }
        }

        protected override string Name        => nameof(QueueHelper);
        protected override string DisplayName => $"Queueing {_dutyMode}: {_content?.Name}";

        internal override void Stop()
        {
            if (State == ActionState.Running)
                Svc.Log.Info($"Done Queueing: {_dutyMode}: {_content?.Name}");
            _content = null;
            _allConditionsMetToJoin = false;
            _turnedOffTrustMembers = false;
            _turnedOnConfigMembers = false;
            _dutyMode = DutyMode.None;

            base.Stop();
        }

        private static Content? _content = null;
        private static DutyMode _dutyMode = DutyMode.None;
        private AddonContentsFinder* _addonContentsFinder = null;
        private bool _allConditionsMetToJoin = false;
        private bool _turnedOffTrustMembers = false;
        private bool _turnedOnConfigMembers = false;

        private static bool ContentsFinderConfirm()
        {
            if (GenericHelpers.TryGetAddonByName("ContentsFinderConfirm", out AtkUnitBase* addonContentsFinderConfirm) && GenericHelpers.IsAddonReady(addonContentsFinderConfirm))
            {
                Svc.Log.Debug("Queue Helper - Confirming DutyPop");
                AddonHelper.FireCallBack(addonContentsFinderConfirm, true, 8);
                return true;
            }
            return false;
        }

        private void QueueTrust()
        {
            if (TrustHelper.State == ActionState.Running) return;

            AgentDawn* agentDawn = AgentDawn.Instance();
            if (agentDawn == null) return;

            if (!agentDawn->IsAddonReady())
            {
                if (!EzThrottler.Throttle("OpenDawn", 5000)) return;

                // AgentHUD.Instance() 與 RaptureAtkModule.Instance() 都會在 UIModule 尚未建立時回 null
                // (產生器出來的實作就是 agentModule == null ? null : ...,RaptureAtkModule 亦同),
                // 原本兩者都無條件解參考。取進區域變數、判空後同幀即用;為 null 時安靜跳過本次
                // 處理,下次節流放行時再試。
                AgentHUD* agentHud = AgentHUD.Instance();
                if (agentHud == null || !agentHud->IsMainCommandEnabled(82)) return;

                Svc.Log.Debug("Queue Helper - Opening Dawn");
                RaptureAtkModule* raptureAtkModule = RaptureAtkModule.Instance();
                if (raptureAtkModule != null)
                    raptureAtkModule->OpenDawn(_content.RowId);
                return;
            }

            // IsAddonReady() 只代表附加介面已就緒，不保證代理人的 Data 資料區塊已配置：
            // 兩者生命週期不同步。每次重取、顯式判空、同幀即用；為 null 時安靜跳過本次
            // 處理，下一幀（節流後）再試。
            var dawnData = agentDawn->Data;
            if (dawnData == null) return;

            if (dawnData->ContentData.ExpansionCount < (_content!.ExVersion - 2))
            {
                Svc.Log.Debug($"Queue Helper - You do not have expansion: {_content.ExVersion} unlocked stopping");
                Stop();
                return;
            }

            if ((byte) agentDawn->SelectedContentId != _content.DawnRowId)
            {
                Svc.Log.Debug($"Queue Helper - Clicking: {_content.EnglishName} at {_content.RowId} with dawn {_content.DawnRowId} instead of {agentDawn->SelectedContentId}");
                RaptureAtkModule* raptureAtkModule = RaptureAtkModule.Instance();
                if (raptureAtkModule != null)
                    raptureAtkModule->OpenDawn(_content.RowId);
            }
            else if (!_turnedOffTrustMembers)
            {
                if (EzThrottler.Throttle("_turnedOffTrustMembers", 500))
                {
                    dawnData->PartyData.ClearParty();
                    agentDawn->UpdateAddon();
                    SchedulerHelper.ScheduleAction("_turnedOffTrustMembers", () => _turnedOffTrustMembers = true, 250);
                }
            }
            else if (!_turnedOnConfigMembers)
            {
                if (EzThrottler.Throttle("_turnedOnConfigMembers", 500))
                {
                    AgentDawnInterface.DawnMemberEntry* curMembers = dawnData->MemberData.GetMembers(dawnData->MemberData.CurrentMembersIndex);
                    var                                 members    = Plugin.Configuration.SelectedTrustMembers;
                    if (members.Count(x => x is not null) == 3)
                        members.OrderBy(x => TrustHelper.Members[(TrustMemberName)x!].Role)
                               .Each(member =>
                                     {
                                         if (member != null)
                                         {
                                             byte                               index       = TrustHelper.Members[(TrustMemberName)member].Index;
                                             AgentDawnInterface.DawnMemberEntry memberEntry = curMembers[index];

                                             dawnData->PartyData.AddMember(index, &memberEntry);
                                         }
                                     });
                    agentDawn->UpdateAddon();
                    SchedulerHelper.ScheduleAction("_turnedOnConfigMembers", () => _turnedOnConfigMembers = true, 250);
                }
            }
            else if(EzThrottler.Throttle("ClickRegisterButton", 10000))
            {
                Svc.Log.Debug($"Queue Helper - Clicking: Register For Duty");
                agentDawn->RegisterForDuty();
            }
        }

        private void QueueSupport()
        {
            AgentDawnStory* agentDawnStory = AgentDawnStory.Instance();
            if (agentDawnStory == null) return;

            if (!agentDawnStory->IsAddonReady())
            {
                if (!EzThrottler.Throttle("OpenDawnStory", 5000)) return;

                // 同 QueueTrust:AgentHUD 與 RaptureAtkModule 在 UIModule 未建立時都會回 null。
                AgentHUD* agentHud = AgentHUD.Instance();
                if (agentHud == null || !agentHud->IsMainCommandEnabled(91)) return;
                
                Svc.Log.Debug("Queue Helper - Opening DawnStory");
                RaptureAtkModule* raptureAtkModule = RaptureAtkModule.Instance();
                if (raptureAtkModule != null)
                    raptureAtkModule->OpenDawnStory(_content.Id);
                return;
            }

            // IsAddonReady() 只代表附加介面已就緒，不保證代理人的 Data 資料區塊已配置：
            // 兩者生命週期不同步。每次重取、顯式判空、同幀即用；為 null 時安靜跳過本次
            // 處理，下一幀（節流後）再試。
            var dawnStoryData = agentDawnStory->Data;
            if (dawnStoryData == null) return;

            if (dawnStoryData->ContentData.ExpansionCount <= _content!.ExVersion)
            {
                Svc.Log.Debug($"Queue Helper - You do not have expansion: {_content.ExVersion} unlocked. stopping");
                Stop();
                return;
            }

            if (dawnStoryData->ContentData.ContentEntries[dawnStoryData->ContentData.SelectedContentEntry].ContentFinderConditionId != _content.RowId)
            {
                Svc.Log.Debug($"Queue Helper - Clicking: {_content.EnglishName} {_content.RowId}");// instead of {dawnStoryData->ContentData.ContentEntries[dawnStoryData->ContentData.SelectedContentEntry].ContentFinderConditionId}");

                RaptureAtkModule* raptureAtkModule = RaptureAtkModule.Instance();
                if (raptureAtkModule != null)
                    raptureAtkModule->OpenDawnStory(_content.RowId);
            }
            else if(EzThrottler.Throttle("ClickRegisterButton", 10000))
            {
                Svc.Log.Debug($"Queue Helper - Clicking: Register For Duty");
                agentDawnStory->RegisterForDuty();
            }
        }

        /// <summary>
        /// 取出副本列表目前選取項目的顯示名稱;取不到就回 "?"。
        ///
        /// 這是純診斷用途,原本整條鏈零檢查:
        ///   items[(int)DutyList->SelectedItemIndex].Renderer->GetTextNodeById(5)->GetAsAtkTextNode()->NodeText
        /// 三個問題:
        ///  ① SelectedItemIndex 沿用 AtkComponentList 的語意,**沒有選取時是 -1**,
        ///     而且沒有任何上界比對 → 直接丟 ArgumentOutOfRangeException。
        ///  ② Renderer 可能是 null,而 GetTextNodeById 是 [MemberFunction] 原生呼叫,
        ///     對 null 呼叫即 AccessViolationException(corrupted-state,try/catch 攔不到)。
        ///  ③ GetTextNodeById 找不到節點時回 null,原本直接再接 GetAsAtkTextNode()。
        /// 回 "?" 而不是空字串,是為了讓診斷訊息看得出「不知道」而不是「名字是空的」。
        /// </summary>
        private string GetSelectedDutyListItemName(List<AtkComponentTreeListItem> items)
        {
            if (_addonContentsFinder == null)
                return "?";

            var dutyList = _addonContentsFinder->DutyList;
            if (dutyList == null)
                return "?";

            var index = dutyList->SelectedItemIndex;
            if (index < 0 || index >= items.Count)
                return "?";

            var renderer = items[index].Renderer;
            if (renderer == null)
                return "?";

            var textNode = renderer->GetTextNodeById(5);
            if (textNode == null || textNode->AtkResNode.Type != NodeType.Text)
                return "?";

            // 🔴 ToString() 不剝 SeString payload：副本名裡的 payload 會解出 U+FFFD 或雜字元，
            //    讓這行診斷訊息看起來像記憶體壞掉。改走 GetText()（MemoryHelper.ReadSeString
            //    → 只保留 TextPayload）。這站不餵守衛，所以原本只是髒 log、不會讓功能停擺。
            // ⚠️ StringPtr 判空不能省（AsSpan() 會建出長度非零、指向位址 0 的 Span）；
            //    取不到就回 "?"，與本方法既有的「不知道」慣例一致。
            if (!textNode->NodeText.StringPtr.HasValue)
                return "?";

            return textNode->NodeText.GetText().Replace("...", "");
        }

        /// <summary>
        /// 讀 <see cref="AtkValue"/> 的字串值,並把 SeString 控制序列攤成畫面上看得到的文字。
        /// </summary>
        /// <remarks>
        /// 🔴 原本這裡走的是 <c>GetValueAsString()</c>(底層是 <c>CStringPointer.ToString()</c>,
        /// 把整段位元組當 UTF-8 硬解、完全不剝 payload),再用三個 <c>Replace</c> 把已知的控制序列敲掉:
        /// <c>02 1A 02 02 03</c> 與 <c>02 1A 02 01 03</c>(斜體開／關)換成空字串、
        /// <c>02 1F 01 03</c>(SeHyphen)換成 U+2013 破折號。
        /// <b>那三個 Replace 是承重的</b> —— 後面 <c>selectedDutyName != _content.Name</c>
        /// 的比對要靠它們才對得上。
        /// <para>
        /// ⚠️ 所以這裡<b>不能</b>換成 ECommons 的 <c>GetText()</c>:那支只收 <c>TextPayload</c>
        /// (外加一個 <c>02 1D 01 03</c> 的特例),會把 SeHyphen 整個丟掉 ⇒ 比對恆假 ⇒
        /// 排隊助手會不停對 ContentsFinder 送 (true, 12, 1) 清掉使用者的選取。
        /// 改成真的解析 SeString(<see cref="SeStringTextExtractor.ExtractDisplayText"/>)之後,
        /// 對「純文字／斜體開關／SeHyphen」三種形狀的輸出與原本的 Replace 鏈逐字相同,
        /// 而且其餘 payload(圖示、顏色、自動翻譯…)也一併不會再漏進比對字串裡。
        /// </para>
        /// <para>
        /// 📌 只有 <c>String</c>／<c>ManagedString</c>／<c>String8</c> 這三種型別的 union 欄位
        /// 是「UTF-8 位元組指標」,才可能夾帶 payload;其餘型別(Int／Bool／Float／WideString…)
        /// 一律沿用 <c>GetValueAsString()</c>,行為與改動前逐字相同。
        /// </para>
        /// <para>
        /// ⚠️ 守衛 <c>IsTextCorrupt</c> 仍然有效:真正的記憶體變動會讓 <c>TextPayload</c> 的
        /// <c>Encoding.UTF8.GetString</c> 走 replacement fallback 解出 U+FFFD。
        /// </para>
        /// </remarks>
        private static string ReadAtkValueDisplayText(AtkValue* value)
        {
            if (value == null)
                return string.Empty;

            AtkValueType type = value->Type;
            if (type != AtkValueType.String && type != AtkValueType.ManagedString && type != AtkValueType.String8)
                return value->GetValueAsString();

            // 🔴 指標判空不能省:Type 說是字串不代表 union 欄位有值,而 SeString 解析會從那個位址
            //    一路讀到 0 為止。GetValueAsString() 這一路是靠 CreateReadOnlySpanFromNullTerminated
            //    對 null 回空 Span 才安全,這裡自己解位址所以要自己擋。
            if (!value->String.HasValue)
                return string.Empty;

            return SeStringTextExtractor.ExtractDisplayText(
                MemoryHelper.ReadSeStringNullTerminated((nint)value->String.Value));
        }

        private void QueueRegular()
        {
            // ContentsFinder 是 [StaticAddress] 解析出來的靜態實例,特徵碼失配時 Resolve 會靜默
            // 留下 null;AgentContentsFinder.Instance() 則在 UIModule 未建立時回 null。本方法原本
            // 有六處無條件解參考(行號會隨編輯漂移,不寫死),而且整段被 try/catch 包住 ——
            // ⚠️ AccessViolationException 在 .NET Core 是 corrupted-state exception,catch 攔不到,
            //    例外隔離不算防護。改成方法開頭各取一次、判空後同幀即用;兩者任一為 null 時
            //    整個 tick 不動作(排隊變成靜默 no-op 而不是崩潰)。
            ContentsFinder*      contentsFinder      = ContentsFinder.Instance();
            AgentContentsFinder* agentContentsFinder = AgentContentsFinder.Instance();
            if (contentsFinder == null || agentContentsFinder == null)
                return;

            if (contentsFinder->IsUnrestrictedParty != Plugin.Configuration.Unsynced)
            {
                Svc.Log.Debug("Queue Helper - Setting UnrestrictedParty");
                contentsFinder->IsUnrestrictedParty = Plugin.Configuration.Unsynced;
                return;
            }

            GenericHelpers.TryGetAddonByName("ContentsFinder", out _addonContentsFinder);
            if (!_allConditionsMetToJoin && (_addonContentsFinder == null || !GenericHelpers.IsAddonReady((AtkUnitBase*)_addonContentsFinder)))
            {
                AgentHUD* agentHud = AgentHUD.Instance();
                if (agentHud == null || !agentHud->IsMainCommandEnabled(33))
                    return;
                Svc.Log.Debug($"Queue Helper - Opening ContentsFinder to {_content!.Name}");
                agentContentsFinder->OpenRegularDuty(_content.ContentFinderCondition);
                return;
            }

            // 上面那個 && 在 _allConditionsMetToJoin 為 true 時會短路,addon 的 null 檢查整個被跳過;
            // 而 TryGetAddonByName 找不到 addon 時會把 out 參數設成 null ——
            // 也就是排隊條件都符合之後,ContentsFinder 一關閉,下一 tick 就會對空指標取 DutyList。
            // 這裡補一道與 _allConditionsMetToJoin 無關的閘,失敗形式是「這一 tick 不動作」。
            if (_addonContentsFinder == null || _addonContentsFinder->DutyList == null)
                return;

            if (_addonContentsFinder->DutyList->Items.LongCount == 0)
                return;

            var vectorDutyListItems = _addonContentsFinder->DutyList->Items;
            List<AtkComponentTreeListItem> listAtkComponentTreeListItems = [];
            if (vectorDutyListItems.Count == 0)
                return;
            
            // 向量裡的項目指標可能是空的,解參考前先擋掉(原本是無條件 *p.Value)。
            vectorDutyListItems.ForEach(pointAtkComponentTreeListItem =>
            {
                if (pointAtkComponentTreeListItem.Value != null)
                    listAtkComponentTreeListItems.Add(*(pointAtkComponentTreeListItem.Value));
            });

            if (!_allConditionsMetToJoin && agentContentsFinder->SelectedDuty.Id != _content!.ContentFinderCondition)
            {
                // 原本這行把整條原生解參考鏈寫在字串插值裡 —— 插值一律先求值,
                // 所以不管記錄等級開到多低都會執行。先取進區域變數,插值只用區域變數。
                var wrongSelectionName = GetSelectedDutyListItemName(listAtkComponentTreeListItems);
                Svc.Log.Debug($"Queue Helper - Opening ContentsFinder to {_content.Name} because we have the wrong selection of {wrongSelectionName}");
                agentContentsFinder->OpenRegularDuty(_content.ContentFinderCondition);
                EzThrottler.Throttle("QueueHelper", 500, true);
                return;
            }

            // AtkValues 是原生指標陣列,沒有 Length 可以靠 —— 索引 18 必須先比對 AtkValuesCount。
            // 越界讀到的是垃圾型別 + 垃圾指標,而 GetValueAsString() 會照那個型別把它當字串指標解參考。
            // 取不到時視為「目前沒有選取任何副本」,走既有的 SelectDuty 分支(與原本空字串的行為一致)。
            var selectedDutyName = string.Empty;
            if (_addonContentsFinder->AtkValues != null && _addonContentsFinder->AtkValuesCount > 18)
                selectedDutyName = ReadAtkValueDisplayText(_addonContentsFinder->AtkValues + 18);
            // 讀到 U+FFFD ＝ ContentsFinder 的記憶體正在變動(關閉中或重繪中),這一幀不碰:
            // 亂碼必定不等於 _content.Name 又不是空字串,原本會直接對它送 (true, 12, 1) 把選取清掉。
            // 正常(文字完整)路徑一行都沒動;250 毫秒節流放行後下一輪再讀一次。
            if (AddonPressGuard.IsTextCorrupt("ContentsFinder.SelectedDuty", selectedDutyName))
                return;

            if (selectedDutyName != _content!.Name && !string.IsNullOrEmpty(selectedDutyName))
            {
                Svc.Log.Debug($"Queue Helper - We have {selectedDutyName} selected, not {_content.Name}, Clearing.");
                AddonHelper.FireCallBack((AtkUnitBase*)_addonContentsFinder, true, 12, 1);
                return;
            }

            if (string.IsNullOrEmpty(selectedDutyName))
            {
                Svc.Log.Debug("Queue Helper - Checking Duty");
                SelectDuty(_addonContentsFinder);
                return;
            }

            if (selectedDutyName == _content.Name)
            {
                _allConditionsMetToJoin = true;
                Svc.Log.Debug("Queue Helper - All Conditions Met, Clicking Join");
                AddonHelper.FireCallBack((AtkUnitBase*)_addonContentsFinder, true, 12, 0);

                // 主機端按下報名的同一刻,通知各用戶端準備接受彈出的確認視窗。
                // 未啟用多開 / 非主機端時整段跳過。
                if (MultiboxUtility.Config is { MultiBox: true, Host: true })
                    MultiboxUtility.Server.Queue();
                return;
            }
            Svc.Log.Debug("end");
        }

        protected override void HelperUpdate(IFramework framework)
        {
            if (_content == null || Plugin.InDungeon || Svc.ClientState.TerritoryType == _content?.TerritoryType)
                Stop();

            // Conditions 也是 [StaticAddress] 靜態實例,特徵碼失配時會靜默留下 null,原本直接解參考。
            // 判空條件擺在原本解參考的位置上,以保留 || 的短路順序 —— EzThrottler.Throttle 與
            // ContentsFinderConfirm() 都有副作用,不能被提前或延後求值。
            Conditions* conditions = Conditions.Instance();
            if (!EzThrottler.Throttle("QueueHelper", 250)|| !PlayerHelper.IsReadyFull || ContentsFinderConfirm() || conditions == null || conditions->InDutyQueue) return;

            switch (_dutyMode)
            {
                case DutyMode.Regular:
                case DutyMode.Trial:
                case DutyMode.Raid:
                    try
                    {
                        QueueRegular();
                    }
                    catch (Exception ex)
                    {
                        Svc.Log.Error(ex.ToString());
                    }

                    break;
                case DutyMode.Support:
                    QueueSupport();
                    break;
                case DutyMode.Trust:
                    QueueTrust();
                    break;
            }
        }

        private static uint HeadersCount(int before, List<AtkComponentTreeListItem> list)
        {
            uint count = 0;
            try
            {
                for (int i = 0; i < before; i++)
                {
                    if (list[i].UIntValues[0] == 0 || list[i].UIntValues[0] == 1)
                        count++;
                }
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex.ToString());
            }

            return count;
        }

        private static void SelectDuty(AddonContentsFinder* addonContentsFinder)
        {
            if (addonContentsFinder == null) return;
            
            // DutyList 是 addon 內的元件指標,addon 剛開/正在關的那幾幀可能還是 null。目前唯一的
            // 呼叫端(QueueRegular)上游已判過,但這是 private static、擋不住未來新增的呼叫端,
            // 所以自己再擋一次(失敗形式=這一 tick 不動作)。
            if (addonContentsFinder->DutyList == null) return;

            var vectorDutyListItems = addonContentsFinder->DutyList->Items;
            List<AtkComponentTreeListItem> listAtkComponentTreeListItems = [];
            // 與 QueueRegular 同型:向量裡的項目指標可能是空的,解參考前先擋掉
            // (原本是無條件 *p.Value)。
            vectorDutyListItems.ForEach(pointAtkComponentTreeListItem =>
            {
                if (pointAtkComponentTreeListItem.Value != null)
                    listAtkComponentTreeListItems.Add(*(pointAtkComponentTreeListItem.Value));
            });
            AddonHelper.FireCallBack((AtkUnitBase*)addonContentsFinder, true, 3, HeadersCount(addonContentsFinder->DutyList->SelectedItemIndex, listAtkComponentTreeListItems) + 1); // - (HeadersCount(addonContentsFinder->DutyList->SelectedItemIndex, listAtkComponentTreeListItems) + 1));
        }
    }
}
