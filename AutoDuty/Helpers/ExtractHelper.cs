using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoDuty.Helpers
{
    internal class ExtractHelper : ActiveHelperBase<ExtractHelper>
    {
        protected override string Name        => nameof(ExtractHelper);
        protected override string DisplayName => "Extracting Materia";

        protected override string[] AddonsToClose { get; } = ["Materialize", "MaterializeDialog", "SelectYesno", "SelectString"];

        internal override void Start()
        {
            if (!QuestManager.IsQuestComplete(66174))
                Svc.Log.Info("Materia Extraction requires having completed quest: Forging the Spirit");
            else
            {
                base.Start();

                _stoppingCategory = Plugin.Configuration.AutoExtractAll ? 6 : 0;
            }
        }

        internal override unsafe void Stop()
        {
            _currentCategory = 0;
            _switchedCategory = false;
            base.Stop();
        }

        private int _currentCategory = 0;
        private int _stoppingCategory;
        private bool _switchedCategory = false;

        protected override unsafe void HelperUpdate(IFramework framework)
        {
            if (Plugin.States.HasFlag(PluginState.Navigating) || Plugin.InDungeon)
                Stop();

            if (!EzThrottler.Throttle("Extract", 250))
                return;

            if (Conditions.Instance()->Mounted)
            {
                ActionManager.Instance()->UseAction(ActionType.GeneralAction, 23);
                return;
            }

            Plugin.Action = "Extracting Materia";

            if (InventoryManager.Instance()->GetEmptySlotsInBag() < 1)
            {
                Stop();
                return;
            }

            if (PlayerHelper.IsOccupied)
                return;

            if (GenericHelpers.TryGetAddonByName("MaterializeDialog", out AtkUnitBase* addonMaterializeDialog) && GenericHelpers.IsAddonReady(addonMaterializeDialog))
            {
                // 🔴 這條路徑不經 AddonHelper,所以要自己過守衛。上面那道 250 毫秒節流不是防護:
                //    它記的是時刻不是「這扇窗按過了」,而確認框「關閉中」的那幾幀
                //    TryGetAddonByName 與 IsAddonReady 三關全過 —— 再按一次就是攔不到的存取違規。
                if (AddonPressGuard.TryBeginPress("MaterializeDialog", addonMaterializeDialog, "Materialize"))
                {
                    Svc.Log.Debug("AutoExtract - Confirming MaterializeDialog");
                    new AddonMaster.MaterializeDialog(addonMaterializeDialog).Materialize();
                }

                return;
            }

            if (!GenericHelpers.TryGetAddonByName("Materialize", out AtkUnitBase* addonMaterialize))
                ActionManager.Instance()->UseAction(ActionType.GeneralAction, 14);
            else if (GenericHelpers.IsAddonReady(addonMaterialize))
            {
                if (_currentCategory <= _stoppingCategory)
                {
                    // 🔴 這兩條原本各是 5~6 層的鏈式裸讀。GetNodeById 找不到節點就回 null,
                    // GetAsAtkXxx / GetComponent / GetTextNodeById 全是遊戲原生函式,對 null 的
                    // this 直接解參考 —— 而 AVE 是 corrupted-state exception,try/catch 攔不到。
                    // 另外 NodeList 是**裸指標陣列**,索引前必須自己驗 NodeListCount:
                    // 它沒有任何邊界檢查,超界讀到的是相鄰記憶體而不是例外。
                    var listNode = addonMaterialize->GetNodeById(12);
                    if (listNode == null) return;

                    var list = listNode->GetAsAtkComponentList();

                    if (list == null) return;

                    if (list->UldManager.NodeList == null || list->UldManager.NodeListCount <= 2) return;
                    var spiritbondItemNode = list->UldManager.NodeList[2];
                    if (spiritbondItemNode == null) return;

                    var spiritbondComponent = spiritbondItemNode->GetComponent();
                    if (spiritbondComponent == null) return;

                    // GetTextNodeById 回傳的已經是 AtkTextNode*,但原本還多接了一次
                    // GetAsAtkTextNode()(等於一道型別斷言)。保留那道斷言,只是先判空再呼叫。
                    AtkTextNode* spiritbondTextNode = null;
                    var spiritbondText = spiritbondComponent->GetTextNodeById(5);
                    if (spiritbondText != null) spiritbondTextNode = spiritbondText->GetAsAtkTextNode();

                    var dropdownNode = addonMaterialize->GetNodeById(4);
                    if (dropdownNode == null) return;

                    var dropdown = dropdownNode->GetAsAtkComponentDropdownList();
                    if (dropdown == null) return;

                    if (dropdown->UldManager.NodeList == null || dropdown->UldManager.NodeListCount <= 1) return;
                    var categoryItemNode = dropdown->UldManager.NodeList[1];
                    if (categoryItemNode == null) return;

                    var categoryCheckBox = categoryItemNode->GetAsAtkComponentCheckBox();
                    if (categoryCheckBox == null) return;

                    AtkTextNode* categoryTextNode = null;
                    var categoryText = categoryCheckBox->GetTextNodeById(3);
                    if (categoryText != null) categoryTextNode = categoryText->GetAsAtkTextNode();

                    if (spiritbondTextNode == null || categoryTextNode == null) return;

                    //switch to Category, if not on it
                    if (!_switchedCategory)
                    {
                        Svc.Log.Debug($"AutoExtract - Switching to Category: {_currentCategory}");
                        AddonHelper.FireCallBack(addonMaterialize, false, 1, _currentCategory);
                        _switchedCategory = true;
                        return;
                    }

                    // 讀到 U+FFFD ＝ 視窗記憶體正在變動,這一幀不碰:既不按也不切分類(切分類是不可逆的狀態推進)。
                    // 🔴 ToString() 就是 Encoding.UTF8.GetString(AsSpan())，不剝 SeString payload
                    //    ⇒ 含 payload 的文字必定解出 U+FFFD ⇒ 下面那道守衛永遠成立、精製永遠不動作。
                    //    GetText() 底下是 MemoryHelper.ReadSeString，只保留 TextPayload。
                    // ⚠️ StringPtr 為 null 而 Length 還留著殘值時，AsSpan() 會建出長度非零、
                    //    指向位址 0 的 Span ⇒ 攔不到的存取違規。判空後比照守衛擋下處理：這一幀
                    //    什麼都不做，**不可以**往下走 else 去 _currentCategory++（那是不可逆的推進）。
                    if (!spiritbondTextNode->NodeText.StringPtr.HasValue)
                        return;

                    string spiritbondValue = spiritbondTextNode->NodeText.GetText();
                    if (AddonPressGuard.IsTextCorrupt("Materialize", spiritbondValue))
                        return;

                    if (spiritbondValue.Replace(" ", string.Empty) == "100%")
                    {
                        Svc.Log.Debug($"AutoExtract - Extracting Materia");
                        AddonHelper.FireCallBack(addonMaterialize, true, 2, 0);
                        return;
                    }
                    else
                    {
                        _currentCategory++;
                        _switchedCategory = false;
                    }
                }
                else
                {
                    // Close(true) 也會送 callback;守衛擋下就不關,Stop() 之後 HelperStopUpdate 會用 AddonsToClose 補關。
                    if (AddonPressGuard.TryBeginClose("Materialize", addonMaterialize))
                        addonMaterialize->Close(true);
                    Svc.Log.Info("Extract Materia Finished");
                    Stop();
                }
            }
        }
    }
}
