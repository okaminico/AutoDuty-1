using AutoDuty.Helpers;
using AutoDuty.IPC;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.GameFunctions;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AutoDuty.Windows
{
    using Data;
    using static Data.Classes;
    internal static class MainTab
    {
        internal static ContentPathsManager.ContentPathContainer? DutySelected;
        internal static Content? SelectedDuty;
        internal static int SelectedPath = -1;
        internal static readonly (string Normal, string GameFont) Digits = ("0123456789", "");

        internal static RunContext? BuildManualRunContext(bool startFromZero = true, bool bareMode = false)
        {
            // During transition, fall back to legacy global selection so this builder is usable
            // before MainTab fully owns the selection state.
            var duty = Plugin.LevelingEnabled
                ? Plugin.CurrentTerritoryContent
                : (SelectedDuty ?? Plugin.CurrentTerritoryContent);
            if (duty == null)
                return null;

            var pathIndex = SelectedPath >= 0 ? SelectedPath : Plugin.CurrentPath;

            return new RunContext
            {
                Source = RunSource.Manual,
                Duty = duty,
                PathIndex = pathIndex,
                // Keep manual dashboard runs dynamic: effective loops should come from current UI/config value.
                Loops = 0,
                StartFromZero = startFromZero,
                BareMode = bareMode,
                PlannerItemIndex = -1,
                PersistLoopsToConfig = false,
            };
        }

        private static int _currentStepIndex = -1;
        // 「找不到路徑檔」訊息裡給使用者看的網址。指向本 fork,與實際下載來源
        // (GitHubHelper.PathRepoBaseUrl)保持同一個 repo/分支。
        private static readonly string _pathsURL = "https://github.com/ffxiv-tc-port/AutoDuty/tree/tc-7.20/AutoDuty/Paths";

        // New search text field for filtering duties
        private static string _searchText = string.Empty;

        internal static void Draw()
        {
            if (MainWindow.CurrentTabName != "Main")
                MainWindow.CurrentTabName = "Main";
            var dutyMode = Plugin.Configuration.DutyModeEnum;
            var levelingMode = Plugin.LevelingModeEnum;

            static void DrawSearchBar()
            {
                // Set the maximum search to 10 characters
                uint inputMaxLength = 10;
                
                // Calculate the X width of the maximum amount of search characters
                Vector2 _characterWidth = ImGui.CalcTextSize("W");
                float inputMaxWidth = ImGui.CalcTextSize("W").X * inputMaxLength;
                
                // Set the width of the search box to the calculated width
                ImGui.SetNextItemWidth(inputMaxWidth);
                
                ImGui.InputTextWithHint("##search", "Search duties...".Loc(), ref _searchText, (int)inputMaxLength);

                // Apply filtering based on the search text
                if (_searchText.Length > 0)
                {
                    // Trim and convert to lowercase for case-insensitive search
                    _searchText = _searchText.Trim().ToLower();
                }
            }

            static void DrawPathSelection()
            {
                if (Plugin.CurrentTerritoryContent == null || !PlayerHelper.IsReady)
                    return;

                using var d = ImRaii.Disabled(Plugin is { InDungeon: true, Stage: > 0 });

                if (ContentPathsManager.DictionaryPaths.TryGetValue(Plugin.CurrentTerritoryContent.TerritoryType, out var container))
                {
                    List<ContentPathsManager.DutyPath> curPaths = container.Paths;
                    if (curPaths.Count > 1)
                    {
                        int                              curPath       = Math.Clamp(Plugin.CurrentPath, 0, curPaths.Count - 1);

                        Dictionary<string, JobWithRole>? pathSelection    = null;
                        JobWithRole                      curJob = Svc.Objects.LocalPlayer.GetJob().JobToJobWithRole();
                        using (ImRaii.Disabled(curPath <= 0 ||
                                               !Plugin.Configuration.PathSelectionsByPath.ContainsKey(Plugin.CurrentTerritoryContent.TerritoryType) || 
                                               !(pathSelection = Plugin.Configuration.PathSelectionsByPath[Plugin.CurrentTerritoryContent.TerritoryType]).Any(kvp => kvp.Value.HasJob(Svc.Objects.LocalPlayer.GetJob()))))
                        {
                            if (ImGui.Button("Clear Saved Path".Loc()))
                            {
                                foreach (KeyValuePair<string, JobWithRole> keyValuePair in pathSelection) 
                                    pathSelection[keyValuePair.Key] &= ~curJob;

                                PathSelectionHelper.RebuildDefaultPaths(Plugin.CurrentTerritoryContent.TerritoryType);
                                Plugin.Configuration.Save();
                                if (!Plugin.InDungeon)
                                    container.SelectPath(out Plugin.CurrentPath);
                            }
                        }
                        ImGui.SameLine();
                        ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X);
                        if (ImGui.BeginCombo("##SelectedPath", curPaths[curPath].Name))
                        {
                            foreach ((ContentPathsManager.DutyPath Value, int Index) path in curPaths.Select((value, index) => (Value: value, Index: index)))
                            {
                                if (ImGui.Selectable(path.Value.Name))
                                {
                                    curPath = path.Index;
                                    SelectedPath = curPath;

                                    // Do not disturb an active planner run.
                                    if (!(Plugin.States.HasFlag(PluginState.Looping) && Plugin.ActiveRunContext?.Source == RunSource.Planner))
                                    {
                                        PathSelectionHelper.AddPathSelectionEntry(Plugin.CurrentTerritoryContent!.TerritoryType);
                                        Dictionary<string, JobWithRole> pathJobs = Plugin.Configuration.PathSelectionsByPath[Plugin.CurrentTerritoryContent.TerritoryType]!;
                                        pathJobs.TryAdd(path.Value.FileName, JobWithRole.None);

                                        foreach (string jobsKey in pathJobs.Keys)
                                            pathJobs[jobsKey] &= ~curJob;

                                        pathJobs[path.Value.FileName] |= curJob;

                                        PathSelectionHelper.RebuildDefaultPaths(Plugin.CurrentTerritoryContent.TerritoryType);

                                        Plugin.Configuration.Save();
                                        Plugin.CurrentPath = curPath;
                                        Plugin.LoadPath();
                                    }
                                }
                                if (ImGui.IsItemHovered() && !path.Value.PathFile.Meta.Notes.All(x => x.IsNullOrEmpty()))
                                    ImGui.SetTooltip(string.Join("\n", path.Value.PathFile.Meta.Notes));
                            }
                            ImGui.EndCombo();
                        }
                        ImGui.PopItemWidth();
                        
                        if (ImGui.IsItemHovered() && !curPaths[curPath].PathFile.Meta.Notes.All(x => x.IsNullOrEmpty()))
                            ImGui.SetTooltip(string.Join("\n", curPaths[curPath].PathFile.Meta.Notes));
                        
                    }
                }
            }

            if (Plugin.InDungeon)
            {
                if (Plugin.CurrentTerritoryContent == null)
                    Plugin.LoadPath();
                else
                {
                    ImGui.AlignTextToFramePadding();
                    var progress = VNavmesh_IPCSubscriber.IsEnabled ? VNavmesh_IPCSubscriber.Nav_BuildProgress() : 0;
                    if (progress >= 0)
                    {
                        ImGui.Text("?? Mesh: Loading: ".Loc(Plugin.CurrentTerritoryContent.Name));
                        ImGui.SameLine();
                        ImGui.ProgressBar(progress, new Vector2(200, 0));
                    }
                    else
                        ImGui.Text("?? Mesh: Loaded Path: ??".Loc(Plugin.CurrentTerritoryContent.Name, ContentPathsManager.DictionaryPaths.ContainsKey(Plugin.CurrentTerritoryContent.TerritoryType) ? "Loaded".Loc() : "None".Loc()));

                    ImGui.Separator();
                    ImGui.Spacing();

                    if (dutyMode == DutyMode.Trust && Plugin.CurrentTerritoryContent != null)
                    {
                        ImGui.Columns(3);
                        using (ImRaii.Disabled()) 
                            DrawTrustMembers(Plugin.CurrentTerritoryContent);
                        ImGui.Columns(1);
                        ImGui.Spacing();
                    }

                    DrawPathSelection();
                    if (!Plugin.States.HasFlag(PluginState.Looping) && !Plugin.Overlay.IsOpen)
                        MainWindow.GotoAndActions();
                    using (ImRaii.Disabled(!VNavmesh_IPCSubscriber.IsEnabled || !Plugin.InDungeon || !VNavmesh_IPCSubscriber.Nav_IsReady() || !BossMod_IPCSubscriber.IsEnabled))
                    {
                        using (ImRaii.Disabled(!Plugin.InDungeon || !ContentPathsManager.DictionaryPaths.ContainsKey(Plugin.CurrentTerritoryContent.TerritoryType)))
                        {
                            if (Plugin.Stage == 0)
                            {
                                if (ImGui.Button("Start".Loc()))
                                {
                                    Plugin.LoadPath();
                                    _currentStepIndex = -1;
                                    var startFromZero = !Plugin.MainListClicked;
                                    var ctx = Plugin.BuildCommandRunContext(Svc.ClientState.TerritoryType, loops: 0, startFromZero: startFromZero, bareMode: false, source: RunSource.Manual, persistLoopsToConfig: false);
                                    if (ctx != null)
                                        Plugin.Run(ctx);
                                    else
                                        Plugin.Run(Svc.ClientState.TerritoryType, 0, startFromZero);
                                }
                            }
                            else
                                MainWindow.StopResumePause();
                            ImGui.SameLine(0, 15);
                        }
                        ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X);
                        MainWindow.LoopsConfig();
                        ImGui.PopItemWidth();

                        if (!ImGui.BeginListBox("##MainList", new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetContentRegionAvail().Y))) return;

                        if ((VNavmesh_IPCSubscriber.IsEnabled || Plugin.Configuration.UsingAlternativeMovementPlugin) && (BossMod_IPCSubscriber.IsEnabled || Plugin.Configuration.UsingAlternativeBossPlugin) && (ReflectionHelper.RotationSolver_Reflection.RotationSolverEnabled || BossMod_IPCSubscriber.IsEnabled || Plugin.Configuration.UsingAlternativeRotationPlugin))
                        {
                            foreach (var item in Plugin.Actions.Select((Value, Index) => (Value, Index)))
                            {
                                item.Value.DrawCustomText(item.Index, () => ItemClicked(item));
                                //var text = item.Value.Name.StartsWith("<--", StringComparison.InvariantCultureIgnoreCase) ? item.Value.Note : $"{item.Value.ToCustomString()}";
                                ////////////////////////////////////////////////////////////////
                            }
                            if (_currentStepIndex != Plugin.Indexer && _currentStepIndex > -1 && Plugin.Stage > 0)
                            {
                                var lineHeight = ImGui.GetTextLineHeightWithSpacing();
                                _currentStepIndex = Plugin.Indexer;
                                if (_currentStepIndex > 1)
                                    ImGui.SetScrollY((_currentStepIndex - 1) * lineHeight);
                            }
                            else if (_currentStepIndex == -1 && Plugin.Stage > 0)
                            {
                                _currentStepIndex = 0;
                                ImGui.SetScrollY(_currentStepIndex);
                            }
                            if (Plugin.InDungeon && Plugin.Actions.Count < 1 && !ContentPathsManager.DictionaryPaths.ContainsKey(Plugin.CurrentTerritoryContent.TerritoryType))
                                ImGui.TextColored(new Vector4(0, 255, 0, 1), "No Path file was found for:\n??\n(??.json)\nin the Paths Folder:\n??\nPlease download from:\n??\nor Create in the Build Tab".Loc(TerritoryName.GetTerritoryName(Plugin.CurrentTerritoryContent.TerritoryType).Split('|')[1].Trim(), Plugin.CurrentTerritoryContent.TerritoryType, Plugin.PathsDirectory.FullName.Replace('\\', '/'), _pathsURL));
                        }
                        else
                        {
                            if (!VNavmesh_IPCSubscriber.IsEnabled && !Plugin.Configuration.UsingAlternativeMovementPlugin)
                                ImGui.TextColored(new Vector4(255, 0, 0, 1), "AutoDuty Requires VNavmesh plugin to be Installed and Loaded\nPlease add 3rd party repo:\nhttps://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json".Loc());
                            if (!BossMod_IPCSubscriber.IsEnabled && !Plugin.Configuration.UsingAlternativeBossPlugin)
                                ImGui.TextColored(new Vector4(255, 0, 0, 1), "AutoDuty Requires BossMod plugin to be Installed and Loaded\nPlease add 3rd party repo:\nhttps://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json".Loc());
                            if (!Wrath_IPCSubscriber.IsEnabled && !ReflectionHelper.RotationSolver_Reflection.RotationSolverEnabled && !BossMod_IPCSubscriber.IsEnabled && !Plugin.Configuration.UsingAlternativeRotationPlugin)
                                ImGui.TextColored(new Vector4(255, 0, 0, 1), "AutoDuty Requires a Rotation plugin to be Installed and Loaded (Either Wrath Combo, Rotation Solver Reborn, or BossMod AutoRotation)".Loc());
                        }
                        ImGui.EndListBox();
                    }
                }
            }
            else
            {
                // Mutual exclusion: if Planner is currently running, Main UI must be inert.
                if (Plugin.States.HasFlag(PluginState.Looping) && Plugin.ActiveRunContext?.Source == RunSource.Planner)
                {
                    ImGui.TextDisabled("Planner is running.".Loc());
                    ImGui.TextDisabled("Main controls are disabled. Stop Planner first.".Loc());
                    return;
                }

                if (!Plugin.States.HasFlag(PluginState.Looping) && !Plugin.Overlay.IsOpen)
                    MainWindow.GotoAndActions();

                // Planner status line (A4-b)
                if (Plugin.PlannerActive && Plugin.Configuration.PlannerItems.Count > 0)
                {
                    var idx = Math.Clamp(Plugin.Configuration.PlannerCurrentIndex, 0, Plugin.Configuration.PlannerItems.Count - 1);
                    var tt = Plugin.Configuration.PlannerItems[idx].TerritoryType;
                    var name = ContentHelper.DictionaryContent.TryGetValue(tt, out var c) ? c.Name : $"{tt}";
                    var state = Plugin.Configuration.PlannerPaused ? "paused".Loc() : (Plugin.ActiveRunContext?.Source == RunSource.Planner && Plugin.States.HasFlag(PluginState.Looping) ? "running".Loc() : "idle".Loc());
                    ImGui.TextDisabled("Planner: ??/?? ?? (??)".Loc(idx + 1, Plugin.Configuration.PlannerItems.Count, name, state));
                }

                using (ImRaii.Disabled(Plugin.CurrentTerritoryContent == null || (Plugin.Configuration.DutyModeEnum == DutyMode.Trust && Plugin.Configuration.SelectedTrustMembers.Any(x => x is null))))
                {
                    if (!Plugin.States.HasFlag(PluginState.Looping))
                    {
                        if (ImGui.Button("Run".Loc()))
                        {
                            if (Plugin.Configuration.DutyModeEnum == DutyMode.None)
                                MainWindow.ShowPopup("Error".Loc(), "You must select a version\nof the dungeon to run".Loc());
                            else if (Svc.Party.PartyId > 0 && (Plugin.Configuration.DutyModeEnum == DutyMode.Support || Plugin.Configuration.DutyModeEnum == DutyMode.Squadron || Plugin.Configuration.DutyModeEnum == DutyMode.Trust))
                                MainWindow.ShowPopup("Error".Loc(), "You must not be in a party to run Support, Squadron or Trust".Loc());
                            else if (Plugin.Configuration.DutyModeEnum == DutyMode.Regular && !Plugin.Configuration.Unsynced && !Plugin.Configuration.OverridePartyValidation && Svc.Party.PartyId == 0)
                                MainWindow.ShowPopup("Error".Loc(), "You must be in a group of 4 to run Regular Duties".Loc());
                            else if (Plugin.Configuration.DutyModeEnum == DutyMode.Regular && !Plugin.Configuration.Unsynced && !Plugin.Configuration.OverridePartyValidation && !ObjectHelper.PartyValidation())
                                MainWindow.ShowPopup("Error".Loc(), "You must have the correct party makeup to run Regular Duties".Loc());
                            else if (ContentPathsManager.DictionaryPaths.ContainsKey(Plugin.CurrentTerritoryContent?.TerritoryType ?? 0))
                            {
                                var ctx = BuildManualRunContext();
                                if (ctx != null)
                                    Plugin.Run(ctx);
                            }
                            else
                                MainWindow.ShowPopup("Error".Loc(), "No path was found".Loc());
                        }
                    }
                    else
                        MainWindow.StopResumePause();

                    // Mutual exclusion (policy 3): no queued switching from Planner to Main.
                }
                using (ImRaii.Disabled(Plugin.CurrentTerritoryContent == null))
                {
                    ImGui.SameLine(0, 15);
                    ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X);
                    MainWindow.LoopsConfig();
                    ImGui.PopItemWidth();
                }

                using (ImRaii.Disabled(Plugin.States.HasFlag(PluginState.Looping)))
                {
                    ImGui.TextColored(Plugin.Configuration.DutyModeEnum == DutyMode.None ? new Vector4(1, 0, 0, 1) : new Vector4(0, 1, 0, 1), "Select Duty Mode: ".Loc());
                    ImGui.SameLine(0);
                    ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X);
                    if (ImGui.BeginCombo("##DutyModeEnum", Plugin.Configuration.DutyModeEnum.ToCustomString()))
                    {
                        foreach (DutyMode mode in Enum.GetValues(typeof(DutyMode)))
                        {
                            if (ImGui.Selectable(mode.ToCustomString()))
                            {
                                Plugin.Configuration.DutyModeEnum = mode;
                                Plugin.Configuration.Save();
                            }
                        }
                        ImGui.EndCombo();
                    }
                    ImGui.PopItemWidth();
                    if (Plugin.Configuration.DutyModeEnum != DutyMode.None)
                    {
                        if (Plugin.Configuration.DutyModeEnum == DutyMode.Support || Plugin.Configuration.DutyModeEnum == DutyMode.Trust)
                        {
                            ImGui.TextColored(Plugin.LevelingModeEnum == LevelingMode.None ? new Vector4(1, 0, 0, 1) : new Vector4(0, 1, 0, 1), "Select Leveling Mode: ".Loc());
                            ImGui.SameLine(0);
                            ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X);
                            var levelingModeLabel = "None".Loc();
                            if (Plugin.LevelingModeEnum == LevelingMode.Manual)
                                levelingModeLabel = "Manual".Loc();
                            else if (Plugin.LevelingEnabled)
                                levelingModeLabel = "Auto".Loc();

                            if (ImGui.BeginCombo("##LevelingModeEnum", levelingModeLabel))
                            {
                                if (ImGui.Selectable("None".Loc()))
                                {
                                    Plugin.LevelingModeEnum = LevelingMode.None;
                                    Plugin.Configuration.Save();
                                }
                                if (ImGui.Selectable("Manual".Loc()))
                                {
                                    Plugin.LevelingModeEnum = LevelingMode.Manual;

                                    // Reset stale selection state when switching from Auto -> Manual.
                                    // Manual mode should require an explicit duty selection from the list.
                                    SelectedDuty = null;
                                    SelectedPath = -1;
                                    DutySelected = null;
                                    Plugin.CurrentTerritoryContent = null;

                                    Plugin.Configuration.Save();
                                }
                                if (ImGui.Selectable("Auto".Loc()))
                                {
                                    Plugin.LevelingModeEnum = Plugin.Configuration.DutyModeEnum == DutyMode.Support ? LevelingMode.Support : LevelingMode.Trust;
                                    Plugin.Configuration.Save();
                                    if (Plugin.Configuration.AutoEquipRecommendedGear)
                                        AutoEquipHelper.Invoke();
                                }
                                ImGui.EndCombo();
                            }
                            ImGui.PopItemWidth();

                            if (Plugin.Configuration.DutyModeEnum != DutyMode.Trust)
                                ImGuiComponents.HelpMarker("Leveling Mode will queue you for the most CONSISTENT dungeon considering your lvl + Ilvl. \nIt will NOT always queue you for the highest level dungeon, it follows our stable dungeon list instead.".Loc());
                            else
                                ImGuiComponents.HelpMarker("TRUST Leveling Mode will queue you for the most CONSISTENT dungeon considering your lvl + Ilvl, as well as the LOWEST LEVEL trust members you have, in an attempt to level them all equally.\nIt will NOT always queue you for the highest level dungeon, it follows our stable dungeon list instead.".Loc());
                        }

                        if (Plugin.Configuration.DutyModeEnum == DutyMode.Support && levelingMode == LevelingMode.Support)
                        {
                            if(ImGui.Checkbox("Prefer Trust over Support Leveling".Loc(), ref Plugin.Configuration.PreferTrustOverSupportLeveling))
                                Plugin.Configuration.Save();
                        }

                        if (Plugin.Configuration.DutyModeEnum == DutyMode.Trust && Player.Available)
                        {
                            ImGui.Separator();
                            if (DutySelected != null && DutySelected.Content.TrustMembers.Count > 0)
                            {
                                ImGuiEx.LineCentered(() => ImGuiEx.TextUnderlined("Select your Trust Party".Loc()));
                                

                                TrustHelper.ResetTrustIfInvalid();
                                for (int i = 0; i < Plugin.Configuration.SelectedTrustMembers.Length; i++)
                                {
                                    TrustMemberName? member = Plugin.Configuration.SelectedTrustMembers[i];

                                    if (member is null)
                                        continue;

                                    if (DutySelected.Content.TrustMembers.All(x => x.MemberName != member))
                                    {
                                        Svc.Log.Debug($"Killing {member}");
                                        Plugin.Configuration.SelectedTrustMembers[i] = null;
                                    }
                                }
                                ImGui.Columns(3);
                                using (ImRaii.Disabled(Plugin.TrustLevelingEnabled && TrustHelper.Members.Any(tm => tm.Value.Level < tm.Value.LevelCap)))
                                {
                                    DrawTrustMembers(DutySelected.Content);
                                }
                                //ImGui.Columns(3, null, false);
                                if (DutySelected.Content.TrustMembers.Count == 7)
                                    ImGui.NextColumn();

                                if (ImGui.Button("Refresh".Loc(), new Vector2(ImGui.GetContentRegionAvail().X, 0)))
                                {
                                    if (InventoryHelper.CurrentItemLevel < 370)
                                        Plugin.LevelingModeEnum = LevelingMode.None;
                                    TrustHelper.ClearCachedLevels();

                                    SchedulerHelper.ScheduleAction("Refresh Levels - ShB", () => TrustHelper.GetLevels(ContentHelper.DictionaryContent[837u]), () => TrustHelper.State == ActionState.None);
                                    SchedulerHelper.ScheduleAction("Refresh Levels - EW", () => TrustHelper.GetLevels(ContentHelper.DictionaryContent[952u]), () => TrustHelper.State == ActionState.None);
                                    SchedulerHelper.ScheduleAction("Refresh Levels - DT", () => TrustHelper.GetLevels(ContentHelper.DictionaryContent[1167u]), () => TrustHelper.State == ActionState.None);
                                }
                                ImGui.NextColumn();
                                ImGui.Columns(1);
                            }
                            else if (ImGui.Button("Refresh trust member levels".Loc()))
                            {
                                if (InventoryHelper.CurrentItemLevel < 370)
                                    Plugin.LevelingModeEnum = LevelingMode.None;
                                TrustHelper.ClearCachedLevels();

                                SchedulerHelper.ScheduleAction("Refresh Levels - ShB", () => TrustHelper.GetLevels(ContentHelper.DictionaryContent[837u]), () => TrustHelper.State == ActionState.None);
                                SchedulerHelper.ScheduleAction("Refresh Levels - EW", () => TrustHelper.GetLevels(ContentHelper.DictionaryContent[952u]), () => TrustHelper.State == ActionState.None);
                                SchedulerHelper.ScheduleAction("Refresh Levels - DT", () => TrustHelper.GetLevels(ContentHelper.DictionaryContent[1167u]), () => TrustHelper.State == ActionState.None);
                            }
                        }

                        DrawPathSelection();
                        ImGui.Separator();

                        DrawSearchBar();
                        ImGui.SameLine();
                        if (ImGui.Checkbox("Hide Unavailable Duties".Loc(), ref Plugin.Configuration.HideUnavailableDuties))
                            Plugin.Configuration.Save();
                        if (Plugin.Configuration.DutyModeEnum == DutyMode.Regular || Plugin.Configuration.DutyModeEnum == DutyMode.Trial || Plugin.Configuration.DutyModeEnum == DutyMode.Raid)
                        {
                            if (ImGuiEx.CheckboxWrapped("Unsynced".Loc(), ref Plugin.Configuration.Unsynced))
                                Plugin.Configuration.Save();
                        }
                    }
                    var ilvl = InventoryHelper.CurrentItemLevel;
                    if (!ImGui.BeginListBox("##DutyList", new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetContentRegionAvail().Y))) return;

                    if (VNavmesh_IPCSubscriber.IsEnabled && BossMod_IPCSubscriber.IsEnabled)
                    {
                        if (PlayerHelper.IsReady)
                        {
                            string? dutyListHint = null;

                            if (Plugin.Configuration.DutyModeEnum == DutyMode.None)
                            {
                                dutyListHint = "Please select a duty category above to populate the duty list.".Loc();
                            }
                            else if (Plugin.Configuration.DutyModeEnum is DutyMode.Support or DutyMode.Trust && Plugin.LevelingModeEnum == LevelingMode.None)
                            {
                                dutyListHint = "Please select Manual or Auto above to populate the duty list.".Loc();
                            }
                            else if (Plugin.LevelingEnabled)
                            {
                                if (Player.Job.GetCombatRole() == CombatRole.NonCombat || (Plugin.LevelingModeEnum == LevelingMode.Trust && ilvl < 370) || (Plugin.LevelingModeEnum == LevelingMode.Trust && Plugin.CurrentPlayerItemLevelandClassJob.Value != null && Plugin.CurrentPlayerItemLevelandClassJob.Value != Player.Job))
                                {
                                    Svc.Log.Debug($"You are on a non-compatible job: {Player.Job.GetCombatRole()}, or your doing trust and your iLvl({ilvl}) is below 370, or your iLvl has changed, Disabling Leveling Mode");
                                    Plugin.LevelingModeEnum = LevelingMode.None;
                                    dutyListHint = "Please select Manual or Auto above to populate the duty list.".Loc();
                                }
                                else if (ilvl > 0 && ilvl != Plugin.CurrentPlayerItemLevelandClassJob.Key)
                                {
                                    Svc.Log.Debug($"Your iLvl has changed, Selecting new Duty.");
                                    // Re-apply current auto leveling mode through a single code path
                                    // so duty/path/container state stays consistent.
                                    Plugin.LevelingModeEnum = Plugin.LevelingModeEnum;
                                }
                                else
                                {
                                    ImGuiEx.TextWrapped(new Vector4(0, 1, 0, 1), "Leveling Mode: L?? (i??)".Loc(Player.Level, ilvl));
                                    foreach (var item in LevelingHelper.LevelingDuties.Select((Value, Index) => (Value, Index)))
                                    {
                                        if (Plugin.Configuration.DutyModeEnum == DutyMode.Trust && !item.Value.DutyModes.HasFlag(DutyMode.Trust))
                                            continue;
                                        var disabled = !item.Value.CanRun();
                                        if (!Plugin.Configuration.HideUnavailableDuties || !disabled)
                                        {
                                            using (ImRaii.Disabled(disabled))
                                            {
                                                ImGuiEx.TextWrapped(item.Value == Plugin.CurrentTerritoryContent ? new Vector4(0, 1, 1, 1) : new Vector4(1, 1, 1, 1), "L?? (i??): ??".Loc(item.Value.ClassJobLevelRequired, item.Value.ItemLevelRequired, item.Value.EnglishName));
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                if (Player.Job.GetCombatRole() == CombatRole.NonCombat)
                                    ImGuiEx.TextWrapped(new Vector4(255, 1, 0, 1), "Please switch to a combat job to use AutoDuty.".Loc());
                                else if (Player.Job == Job.BLU && Plugin.Configuration.DutyModeEnum is not (DutyMode.Regular or DutyMode.Trial or DutyMode.Raid))
                                    ImGuiEx.TextWrapped(new Vector4(0, 1, 1, 1), "Blue Mage cannot run Trust, Duty Support, Squadron or Variant dungeons. Please switch jobs or select a different category.".Loc());
                                else
                                {
                                    Dictionary<uint, Content> dictionary = ContentHelper.DictionaryContent
                                        .Where(x => Plugin.Configuration.DutyModeEnum != DutyMode.None && x.Value.DutyModes.HasFlag(Plugin.Configuration.DutyModeEnum))
                                        .ToDictionary();

                                    if (dictionary.Count > 0 && PlayerHelper.IsReady)
                                    {
                                        short level = PlayerHelper.GetCurrentLevelFromSheet();
                                        foreach ((uint _, Content? content) in dictionary)
                                        {
                                            // Apply search filter
                                            if (!string.IsNullOrWhiteSpace(_searchText) && !content.Name.ToLower().Contains(_searchText))
                                                continue;  // Skip duties that do not match the search text

                                            bool canRun = content.CanRun(level);
                                            using (ImRaii.Disabled(!canRun))
                                            {
                                                if (Plugin.Configuration.HideUnavailableDuties && !canRun)
                                                    continue;
                                                var selectedTerritoryType = Plugin.LevelingEnabled
                                                    ? Plugin.CurrentTerritoryContent?.TerritoryType
                                                    : (SelectedDuty ?? Plugin.CurrentTerritoryContent)?.TerritoryType;
                                                var isSelected = selectedTerritoryType == content.TerritoryType;
                                                if (isSelected)
                                                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0, 1, 1, 1));
                                                try
                                                {
                                                    if (ImGui.Selectable("L?? (??) ??".Loc(content.ClassJobLevelRequired, content.TerritoryType, content.Name), isSelected))
                                                    {
                                                        SelectedDuty = content;

                                                        if (ContentPathsManager.DictionaryPaths.TryGetValue(content.TerritoryType, out var container))
                                                        {
                                                            DutySelected = container;
                                                            DutySelected.SelectPath(out SelectedPath);
                                                        }
                                                        else
                                                        {
                                                            DutySelected = null;
                                                            SelectedPath = -1;
                                                        }

                                                        // Selecting a duty while planner is actively running must not
                                                        // mutate the live run state.
                                                        if (!(Plugin.States.HasFlag(PluginState.Looping) && Plugin.ActiveRunContext?.Source == RunSource.Planner))
                                                        {
                                                            Plugin.CurrentTerritoryContent = content;
                                                            if (SelectedPath >= 0)
                                                                Plugin.CurrentPath = SelectedPath;
                                                        }
                                                    }
                                                }
                                                finally
                                                {
                                                    if (isSelected)
                                                        ImGui.PopStyleColor();
                                                }
                                                {
                                                    // no-op: selection handled above
                                                }
                                            }
                                        }
                                    }
                                    else
                                    {
                                        // When DutyMode is None we intentionally render no duties.
                                    }
                                }
                            }

                            if (dutyListHint != null)
                            {
                                ImGui.EndListBox();
                                ImGui.Separator();
                                ImGui.TextDisabled(dutyListHint);
                                return;
                            }
                        }
                        else
                            ImGuiEx.TextWrapped(new Vector4(0, 1, 0, 1), "Busy...".Loc());
                    }
                    else
                    {
                        if (!VNavmesh_IPCSubscriber.IsEnabled)
                            ImGuiEx.TextWrapped(new Vector4(255, 0, 0, 1), "AutoDuty requires vnavmesh plugin to be installed and loaded for proper navigation and movement. Please add 3rd party repo:\nhttps://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json".Loc());
                        if (!BossMod_IPCSubscriber.IsEnabled)
                            ImGuiEx.TextWrapped(new Vector4(255, 0, 0, 1), "AutoDuty requires BossMod plugin to be installed and loaded for proper mechanic handling. Please add 3rd party repo:\nhttps://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json".Loc());
                    }
                    ImGui.EndListBox();
                }
            }
        }

        internal static void DrawTrustMembers(Content content)
        {
            foreach (TrustMember member in content.TrustMembers)
            {
                bool       enabled        = Plugin.Configuration.SelectedTrustMembers.Where(x => x != null).Any(x => x == member.MemberName);
                CombatRole playerRole     = Player.Job.GetCombatRole();
                int        numberSelected = Plugin.Configuration.SelectedTrustMembers.Count(x => x != null);

                TrustMember?[] members = Plugin.Configuration.SelectedTrustMembers.Select(tmn => tmn != null ? TrustHelper.Members[(TrustMemberName)tmn] : null).ToArray();

                bool canSelect = members.CanSelectMember(member, playerRole) && member.Level >= content.ClassJobLevelRequired;

                using (ImRaii.Disabled(!enabled && (numberSelected == 3 || !canSelect)))
                {
                    if (ImGui.Checkbox($"###{member.Index}{content.Id}", ref enabled))
                    {
                        if (enabled)
                        {
                            for (int i = 0; i < 3; i++)
                            {
                                if (Plugin.Configuration.SelectedTrustMembers[i] is null)
                                {
                                    Plugin.Configuration.SelectedTrustMembers[i] = member.MemberName;
                                    break;
                                }
                            }
                        }
                        else
                        {
                            if (Plugin.Configuration.SelectedTrustMembers.Where(x => x != null).Any(x => x == member.MemberName))
                            {
                                int idx = Plugin.Configuration.SelectedTrustMembers.IndexOf(x => x != null && x == member.MemberName);
                                Plugin.Configuration.SelectedTrustMembers[idx] = null;
                            }
                        }

                        Plugin.Configuration.Save();
                    }
                }

                ImGui.SameLine(0, 2);
                ImGui.SetItemAllowOverlap();
                ImGui.TextColored(member.Role switch
                {
                    TrustRole.DPS => ImGuiHelper.RoleDPSColor,
                    TrustRole.Healer => ImGuiHelper.RoleHealerColor,
                    TrustRole.Tank => ImGuiHelper.RoleTankColor,
                    TrustRole.AllRounder => ImGuiHelper.RoleAllRounderColor,
                    _ => Vector4.One
                }, member.Name);
                if (member.Level > 0)
                {
                    ImGui.SameLine(0, 2);
                    ImGuiEx.TextV(member.Level < member.LevelCap ? ImGuiHelper.White : ImGuiHelper.MaxLevelColor, $"{member.Level.ToString().ReplaceByChar(Digits.Normal, Digits.GameFont)}");
                }

                ImGui.NextColumn();
            }
        }

        private static void ItemClicked((PathAction, int) item)
        {
            if (item.Item2 == Plugin.Indexer || item.Item1.Name.StartsWith("<--", StringComparison.InvariantCultureIgnoreCase))
            {
                Plugin.Indexer = -1;
                Plugin.MainListClicked = false;
            }
            else
            {
                Plugin.Indexer = item.Item2;
                Plugin.MainListClicked = true;
            }
        }

        internal static void PathsUpdated()
        {
            DutySelected = null;
        }
    }
}
