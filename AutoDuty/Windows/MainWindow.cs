using System;
using System.Collections.Generic;
using System.Numerics;
using AutoDuty.Helpers;
using AutoDuty.IPC;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ECommons;
using ECommons.DalamudServices;
using ECommons.EzSharedDataManager;
using ECommons.Funding;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
using ECommons.Schedulers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Bindings.ImGui;

namespace AutoDuty.Windows;

public class MainWindow : Window, IDisposable
{
    internal static string CurrentTabName = "";

    private static bool _showPopup = false;
    private static bool _nestedPopup = false;
    private static string _popupText = "";
    private static string _popupTitle = "";
    private static string openTabName = "";

    public MainWindow() : base(
        $"AutoDuty v{Svc.PluginInterface.Manifest.AssemblyVersion}###Autoduty")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(10, 10),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        
        TitleBarButtons.Add(new() { Icon = FontAwesomeIcon.Cog, IconOffset = new(1, 1), Click = _ => OpenTab("Config") });
        TitleBarButtons.Add(new() { ShowTooltip = () => ImGui.SetTooltip("Support Herculezz on Ko-fi".Loc()), Icon = FontAwesomeIcon.Heart, IconOffset = new(1, 1), Click = _ => GenericHelpers.ShellStart("https://ko-fi.com/Herculezz") });
    }

    internal static void SetCurrentTabName(string tabName)
    {
        if (CurrentTabName != tabName)
            CurrentTabName = tabName;
    }

    internal static void OpenTab(string tabName)
    {
        openTabName = tabName;
        _ = new TickScheduler(delegate
        {
            openTabName = "";
        }, 25);
    }

    public void Dispose()
    {
    }

    internal static void Start()
    {
        ImGui.SameLine(0, 5);
    }

    internal static void LoopsConfig()
    {
        var plannerRunning = Plugin.States.HasFlag(PluginState.Looping) && Plugin.ActiveRunContext?.Source == RunSource.Planner;

        if (plannerRunning && Plugin.Configuration.PlannerItems.Count > 0)
        {
            var index = Math.Clamp(Plugin.Configuration.PlannerCurrentIndex, 0, Plugin.Configuration.PlannerItems.Count - 1);
            var item = Plugin.Configuration.PlannerItems[index];
            var runs = Math.Max(1, item.TargetRuns);

            var changed = (Plugin.Configuration.UseSliderInputs && ImGui.SliderInt("Times".Loc(), ref runs, 1, 100))
                          || (!Plugin.Configuration.UseSliderInputs && ImGui.InputInt("Times".Loc(), ref runs));

            if (changed)
            {
                var minRuns = Math.Max(1, item.CompletedRuns);
                item.TargetRuns = Math.Max(minRuns, runs);
                item.CompletedRuns = Math.Clamp(item.CompletedRuns, 0, item.TargetRuns);
            }

            // 滑桿在拖曳期間每一幀都回 true，存檔只能在放開滑鼠（編輯結束）那一刻做一次
            if (ImGui.IsItemDeactivatedAfterEdit())
                Plugin.Configuration.Save();

            return;
        }

        var loopTimes = Math.Max(1, Plugin.Configuration.LoopTimes);
        var loopChanged = (Plugin.Configuration.UseSliderInputs && ImGui.SliderInt("Times".Loc(), ref loopTimes, 1, 100))
                          || (!Plugin.Configuration.UseSliderInputs && ImGui.InputInt("Times".Loc(), ref loopTimes));

        if (loopChanged)
            Plugin.Configuration.LoopTimes = Math.Max(1, loopTimes);

        if (ImGui.IsItemDeactivatedAfterEdit())
            Plugin.Configuration.Save();
    }

    internal static void StopResumePause()
    {
        using (ImRaii.Disabled(!Plugin.States.HasFlag(PluginState.Looping) && !Plugin.States.HasFlag(PluginState.Navigating) && RepairHelper.State != ActionState.Running && GotoHelper.State != ActionState.Running && GotoInnHelper.State != ActionState.Running && GotoBarracksHelper.State != ActionState.Running && GCTurninHelper.State != ActionState.Running && ExtractHelper.State != ActionState.Running && DesynthHelper.State != ActionState.Running))
        {
            if (ImGui.Button("Stop".Loc()))
            {
                Plugin.Stage = Stage.Stopped;
                return;
            }
            ImGui.SameLine(0, 5);
        }

        using (ImRaii.Disabled((!Plugin.States.HasFlag(PluginState.Looping) && !Plugin.States.HasFlag(PluginState.Navigating) && RepairHelper.State != ActionState.Running && GotoHelper.State != ActionState.Running && GotoInnHelper.State != ActionState.Running && GotoBarracksHelper.State != ActionState.Running && GCTurninHelper.State != ActionState.Running && ExtractHelper.State != ActionState.Running && DesynthHelper.State != ActionState.Running) || Plugin.CurrentTerritoryContent == null))
        {
            if (Plugin.Stage == Stage.Paused)
            {
                if (ImGui.Button("Resume".Loc()))
                {
                    Plugin.TaskManager.SetStepMode(false);
                    Plugin.Stage = Plugin.PreviousStage;
                    Plugin.States &= ~PluginState.Paused;
                }
            }
            else
            {
                if (ImGui.Button("Pause".Loc()))
                {
                    Plugin.Stage = Stage.Paused;
                }
            }
        }

        ImGui.SameLine(0, 5);
        DrawSkipStepButton();
    }

    /// <summary>
    /// 「跳過目前步驟」按鈕:中止正在執行的那一步、直接進下一步。
    /// 正在跑的是 Wait 步驟時,順便把已經等掉的時間寫回路徑檔,下次不用再等那麼久。
    /// </summary>
    /// <remarks>
    /// 主視窗與疊加層共用 <see cref="StopResumePause"/>,所以兩邊都會有這個按鈕。
    /// 可按條件見 <c>AutoDuty.CanSkipCurrentStep</c>(暫停中刻意不給按)。
    /// </remarks>
    private static void DrawSkipStepButton()
    {
        bool canSkip = Plugin.CanSkipCurrentStep;

        using (ImRaii.Disabled(!canSkip))
        {
            if (ImGui.Button("Skip Step".Loc()))
                Plugin.SkipCurrentStep();
        }

        // 停用中的項目預設不算 hover,要明講 AllowWhenDisabled 才看得到「為什麼不能按」。
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            return;

        if (!canSkip)
        {
            ImGui.SetTooltip("There is no path step running right now".Loc());
            return;
        }

        string tooltip = "Skips the step that is running right now and continues with the next one".Loc();

        // 「這一步在等多久、已經等了多久」是起疑才會查的資訊 ⇒ 放 tooltip,不占列上版面。
        if (Plugin.TryGetCurrentWaitProgress(out int configuredMs, out int elapsedMs))
            tooltip += "\n" + "This step is waiting; skipping now will change its wait to the ?? seconds already elapsed (was ?? seconds)".Loc(
                           (elapsedMs / 1000f).ToString("0.0"), (configuredMs / 1000f).ToString("0.0"));

        ImGui.SetTooltip(tooltip);
    }

    internal static void GotoAndActions()
    {
        if(Plugin.States.HasFlag(PluginState.Other))
        {
            if(ImGui.Button("Stop".Loc()))
                Plugin.Stage = Stage.Stopped;
            ImGui.SameLine(0,5);
        }

        using (ImRaii.Disabled(Plugin.States.HasFlag(PluginState.Looping) || Plugin.States.HasFlag(PluginState.Navigating)))
        {
            using (ImRaii.Disabled(Plugin.Configuration is { OverrideOverlayButtons: true, GotoButton: false }))
            {
                using (ImRaii.Disabled(Plugin.States.HasFlag(PluginState.Other) && GotoHelper.State != ActionState.Running))
                {
                    if ((GotoHelper.State == ActionState.Running && GCTurninHelper.State != ActionState.Running && RepairHelper.State != ActionState.Running) || MapHelper.State == ActionState.Running || GotoHousingHelper.State == ActionState.Running)
                    {
                        if (ImGui.Button("Stop".Loc()))
                            Plugin.Stage = Stage.Stopped;
                    }
                    else
                    {
                        if (ImGui.Button("Goto".Loc()))
                        {
                            ImGui.OpenPopup("GotoPopup");
                        }
                    }
                }
            }

            if (ImGui.BeginPopup("GotoPopup"))
            {
                if (ImGui.Selectable("Barracks".Loc()))
                {
                    GotoBarracksHelper.Invoke();
                }
                if (ImGui.Selectable("Inn".Loc()))
                {
                    GotoInnHelper.Invoke();
                }
                if (ImGui.Selectable("GCSupply".Loc()))
                {
                    GotoHelper.Invoke(PlayerHelper.GetGrandCompanyTerritoryType(PlayerHelper.GetGrandCompany()), [GCTurninHelper.GCSupplyLocation], 0.25f, 3f);
                }
                if (ImGui.Selectable("Flag Marker".Loc()))
                {
                    MapHelper.MoveToMapMarker();
                }
                if (ImGui.Selectable("Summoning Bell".Loc()))
                {
                    SummoningBellHelper.Invoke(Plugin.Configuration.PreferredSummoningBellEnum);
                }
                if (ImGui.Selectable("Apartment".Loc()))
                {
                    GotoHousingHelper.Invoke(Housing.Apartment);
                }
                if (ImGui.Selectable("Personal Home".Loc()))
                {
                    GotoHousingHelper.Invoke(Housing.Personal_Home);
                }
                if (ImGui.Selectable("FC Estate".Loc()))
                {
                    GotoHousingHelper.Invoke(Housing.FC_Estate);
                }

                if (ImGui.Selectable("Triple Triad Trader".Loc()))
                {
                    GotoHelper.Invoke(TripleTriadCardSellHelper.GoldSaucerTerritoryType, TripleTriadCardSellHelper.TripleTriadCardVendorLocation);
                }
                ImGui.EndPopup();
            }



            ImGui.SameLine(0, 5);
            using (ImRaii.Disabled(!Plugin.Configuration.AutoGCTurnin && !Plugin.Configuration.OverrideOverlayButtons || !Plugin.Configuration.TurninButton))
            {
                using (ImRaii.Disabled(Plugin.States.HasFlag(PluginState.Other) && GCTurninHelper.State != ActionState.Running))
                {
                    if (GCTurninHelper.State == ActionState.Running)
                    {
                        if (ImGui.Button("Stop".Loc()))
                            Plugin.Stage = Stage.Stopped;
                    }
                    else
                    {
                        if (ImGui.Button("TurnIn".Loc()))
                        {
                            if (AutoRetainer_IPCSubscriber.IsEnabled)
                                GCTurninHelper.Invoke();
                            else
                                ShowPopup("Missing Plugin".Loc(), "GC Turnin Requires AutoRetainer plugin. Get @ https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json".Loc());
                        }
                        if (AutoRetainer_IPCSubscriber.IsEnabled)
                            ToolTip("Click to Goto GC Turnin and Invoke AutoRetainer's GC Turnin".Loc());
                        else
                            ToolTip("GC Turnin Requires AutoRetainer plugin. Get @ https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json".Loc());
                    }
                }
            }
            ImGui.SameLine(0, 5);
            using (ImRaii.Disabled(!Plugin.Configuration.AutoDesynth && !Plugin.Configuration.OverrideOverlayButtons || !Plugin.Configuration.DesynthButton))
            {
                using (ImRaii.Disabled(Plugin.States.HasFlag(PluginState.Other) && DesynthHelper.State != ActionState.Running))
                {
                    if (DesynthHelper.State == ActionState.Running)
                    {
                        if (ImGui.Button("Stop".Loc()))
                            Plugin.Stage = Stage.Stopped;
                    }
                    else
                    {
                        if (ImGui.Button("Desynth".Loc()))
                            DesynthHelper.Invoke();
                        ToolTip("Click to Desynth all Items in Inventory".Loc());
                    }
                }
            }
            ImGui.SameLine(0, 5);
            using (ImRaii.Disabled(!Plugin.Configuration.AutoExtract && !Plugin.Configuration.OverrideOverlayButtons || !Plugin.Configuration.ExtractButton))
            {
                using (ImRaii.Disabled(Plugin.States.HasFlag(PluginState.Other) && ExtractHelper.State != ActionState.Running))
                {
                    if (ExtractHelper.State == ActionState.Running)
                    {
                        if (ImGui.Button("Stop".Loc()))
                            Plugin.Stage = Stage.Stopped;
                    }
                    else
                    {
                        if (ImGui.Button("Extract".Loc()))
                        {
                            if (QuestManager.IsQuestComplete(66174))
                                ExtractHelper.Invoke();
                            else
                                ShowPopup("Missing Quest Completion".Loc(), "Materia Extraction requires having completed quest: Forging the Spirit".Loc());
                        }
                        if (QuestManager.IsQuestComplete(66174))
                            ToolTip("Click to Extract Materia".Loc());
                        else
                            ToolTip("Materia Extraction requires having completed quest: Forging the Spirit".Loc());
                    }
                }
            }
            
            ImGui.SameLine(0, 5);
            using (ImRaii.Disabled(!Plugin.Configuration.AutoRepair && !Plugin.Configuration.OverrideOverlayButtons || !Plugin.Configuration.RepairButton))
            {
                using (ImRaii.Disabled(Plugin.States.HasFlag(PluginState.Other) && RepairHelper.State != ActionState.Running))
                {
                    if (RepairHelper.State == ActionState.Running)
                    {
                        if (ImGui.Button("Stop".Loc()))
                            Plugin.Stage = Stage.Stopped;
                    }
                    else
                    {
                        if (ImGui.Button("Repair".Loc()))
                        {
                            if (InventoryHelper.CanRepair(100))
                                RepairHelper.Invoke();
                            //else
                                //ShowPopup("", "");
                        }
                        //if ()
                            ToolTip("Click to Repair".Loc());
                        //else
                            //ToolTip("");
                    }
                }
            }
            ImGui.SameLine(0, 5);
            using (ImRaii.Disabled(!Plugin.Configuration.AutoEquipRecommendedGear && !Plugin.Configuration.OverrideOverlayButtons || !Plugin.Configuration.EquipButton))
            {
                using (ImRaii.Disabled(Plugin.States.HasFlag(PluginState.Other) && AutoEquipHelper.State != ActionState.Running))
                {
                    if (AutoEquipHelper.State == ActionState.Running)
                    {
                        if (ImGui.Button("Stop".Loc()))
                            Plugin.Stage = Stage.Stopped;
                    }
                    else
                    {
                        if (ImGui.Button("Equip".Loc()))
                        {
                            AutoEquipHelper.Invoke();
                            //else
                            //ShowPopup("", "");
                        }

                        //if ()
                        ToolTip("Click to Equip Gear".Loc());
                        //else
                        //ToolTip("");
                    }
                }
            }

            ImGui.SameLine(0, 5);
            using (ImRaii.Disabled(Plugin.Configuration is { AutoOpenCoffers: false, OverrideOverlayButtons: false } || !Plugin.Configuration.CofferButton))
            {
                using (ImRaii.Disabled(Plugin.States.HasFlag(PluginState.Other) && CofferHelper.State != ActionState.Running))
                {
                    if (CofferHelper.State == ActionState.Running)
                    {
                        if (ImGui.Button("Stop".Loc()))
                            Plugin.Stage = Stage.Stopped;
                    }
                    else
                    {
                        if (ImGui.Button("Coffers".Loc()))
                            CofferHelper.Invoke();
                        ToolTip("Click to open coffers".Loc());
                    }
                }
            }
            ImGui.SameLine(0, 5);

            using (ImRaii.Disabled(!Plugin.Configuration.TripleTriadEnabled && (!Plugin.Configuration.OverrideOverlayButtons || !Plugin.Configuration.TTButton)))
            {
                using (ImRaii.Disabled(Plugin.States.HasFlag(PluginState.Other)))
                {
                    if ((GotoHelper.State == ActionState.Running && TripleTriadCardUseHelper.State != ActionState.Running && TripleTriadCardSellHelper.State != ActionState.Running))
                    {
                        if (ImGui.Button("Stop".Loc()))
                            Plugin.Stage = Stage.Stopped;
                    }
                    else
                    {
                        if (ImGui.Button("Triple Triad".Loc()))
                            ImGui.OpenPopup("TTPopup");
                    }
                }
            }

            if (ImGui.BeginPopup("TTPopup"))
            {
                if (ImGui.Selectable("Register TT Cards".Loc()))
                    TripleTriadCardUseHelper.Invoke();
                if (ImGui.Selectable("Sell TT Cards".Loc()))
                    TripleTriadCardSellHelper.Invoke();
                ImGui.EndPopup();
            }
        }
    }

    /// <summary>
    /// 任務逾時計數。逾時不會讓 AutoDuty 停下來(AbortOnTimeout=false),它只是被靜靜放行 ——
    /// 所以這是使用者唯一看得見「剛剛跳過了一步」的地方。沒發生過就整行不畫,不占版面。
    /// </summary>
    /// <remarks>
    /// 掃視得到的是「有沒有、幾次」,細節(是哪個任務、多久以前、工作階段總數)放 tooltip。
    /// 任務名可能真的不存在(ECommons 允許不具名任務),那種情況畫「?」而不是空白 ——
    /// 「不知道」本身要看得見,不能長得像「沒問題」。
    /// </remarks>
    internal static void DrawTaskTimeoutStatus()
    {
        int shown = TaskTimeoutWatcher.RunCount > 0 ? TaskTimeoutWatcher.RunCount : TaskTimeoutWatcher.SessionCount;
        if (shown <= 0)
            return;

        ImGui.TextColored(new Vector4(1f, 0.72f, 0.2f, 1f), "Task timeouts: ??".Loc(shown));

        string lastTask = TaskTimeoutWatcher.LastTaskName ?? "?";
        string ago = TaskTimeoutWatcher.LastTimeoutTick > 0
                         ? $"{(Environment.TickCount64 - TaskTimeoutWatcher.LastTimeoutTick) / 1000}s"
                         : "?";

        ToolTip("A queued step ran past its time limit. AutoDuty does not stop on a timeout, it just moves on, so a boss fight or a treasure chest may have been skipped.".Loc()
                + "\n" + "Last timed-out task: ??".Loc(lastTask)
                + "\n" + "Last occurrence: ?? ago".Loc(ago)
                + "\n" + "This run: ??, this session: ??".Loc(TaskTimeoutWatcher.RunCount, TaskTimeoutWatcher.SessionCount)
                + "\n" + "Full details are in the Dalamud log (search for TaskTimeout).".Loc());
    }

    internal static void ToolTip(string text)
    {
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35f);
            ImGuiEx.Text(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
    }

    internal static void ShowPopup(string popupTitle, string popupText, bool nested = false)
    {
        _popupTitle = popupTitle;
        _popupText = popupText;
        _showPopup = true;
        _nestedPopup = nested;
    }

    internal static void DrawPopup(bool nested = false)
    {
        if (!_showPopup || (_nestedPopup && !nested) || (!_nestedPopup && nested)) return;

        if (!ImGui.IsPopupOpen($"{_popupTitle}###Popup"))
            ImGui.OpenPopup($"{_popupTitle}###Popup");

        Vector2 textSize = ImGui.CalcTextSize(_popupText);
        ImGui.SetNextWindowSize(new(textSize.X + 25, textSize.Y + 100));
        if (ImGui.BeginPopupModal($"{_popupTitle}###Popup", ref _showPopup, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoMove))
        {
            ImGuiEx.TextCentered(_popupText);
            ImGui.Spacing();
            if (ImGuiHelper.CenteredButton("OK".Loc(), .5f, 15))
            {
                _showPopup = false;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private static void KofiLink()
    {
        OpenTab(CurrentTabName);
        if (EzThrottler.Throttle("KofiLink", 15000))
        {
            _ = new TickScheduler(delegate
            {
                GenericHelpers.ShellStart("https://ko-fi.com/Herculezz");
            }, 500);
        }
    }

    //ECommons
    static uint ColorNormal
    {
        get
        {
            var vector1 = ImGuiEx.Vector4FromRGB(0x022594);
            var vector2 = ImGuiEx.Vector4FromRGB(0x940238);

            var gen = GradientColor.Get(vector1, vector2).ToUint();
            var data = EzSharedData.GetOrCreate<uint[]>("ECommonsPatreonBannerRandomColor", [gen]);
            if (!GradientColor.IsColorInRange(data[0].ToVector4(), vector1, vector2))
            {
                data[0] = gen;
            }
            return data[0];
        }
    }
    public static void EzTabBar(string id, string? KoFiTransparent, string openTabName, ImGuiTabBarFlags flags, params (string name, Action function, Vector4? color, bool child)[] tabs)
    {
        ImGui.BeginTabBar(id, flags);
        foreach (var x in tabs)
        {
            if (x.name == null) continue;
            if (x.color != null)
            {
                ImGui.PushStyleColor(ImGuiCol.Tab, x.color.Value);
            }
            // Display text is localized, but the widget ID (after ###) and the navigation key
            // (openTabName / BeginChild) stay on the original English name.
            if (ImGui.BeginTabItem(x.name.Loc() + "###" + x.name, openTabName == x.name ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                if (x.color != null) 
                    ImGui.PopStyleColor();
                if (x.child) 
                    ImGui.BeginChild(x.name + "child");
                x.function();
                if (x.child) 
                    ImGui.EndChild();
                ImGui.EndTabItem();
            }
            else
            {
                if (x.color != null)
                {
                    ImGui.PopStyleColor();
                }
            }
        }
        if (KoFiTransparent != null) PatreonBanner.RightTransparentTab();
        ImGui.EndTabBar();
    }

    private static readonly List<(string, Action, Vector4?, bool)> tabList =
    [("Main", MainTab.Draw, null, false), ("Build", BuildTab.Draw, null, false), ("Paths", PathsTab.Draw, null, false), ("Config", ConfigTab.Draw, null, false), ("排程器", PlannerTab.Draw, null, false), ("Info", InfoTab.Draw, null, false), ("Logs", LogTab.Draw, null, false),("Support AutoDuty", KofiLink, ImGui.ColorConvertU32ToFloat4(ColorNormal), false)
    ];

    public override void Draw()
    {
        DrawPopup();

        if(DalamudInfoHelper.IsOnStaging())
        {
            ImGui.TextColored(GradientColor.Get(ImGuiHelper.ExperimentalColor, ImGuiHelper.ExperimentalColor2, 500), "NOT SUPPORTED ON STAGING.".Loc());
            ImGui.Text("Please type in \"/xlbranch\" and pick Release, then restart the game.".Loc());

            if (!ImGui.CollapsingHeader("Use despite staging. Support will not be given".Loc() + "##stagingHeader"))
                return;
        }

        // 逾時計數畫在分頁列上面 —— 不管使用者在哪個分頁都掃得到,而且沒逾時就完全不畫。
        DrawTaskTimeoutStatus();

        EzTabBar("MainTab", null, openTabName, ImGuiTabBarFlags.None, tabList.ToArray());
    }
}
