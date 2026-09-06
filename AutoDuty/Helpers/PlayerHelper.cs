using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.GameFunctions;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using ECommons;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace AutoDuty.Helpers
{
    using System;
    using System.Collections.Generic;
    using Dalamud.Utility;
    using FFXIVClientStructs.FFXIV.Client.Game.Control;
    using Lumina.Excel.Sheets;

    internal static class PlayerHelper
    {
        internal static unsafe uint GetGrandCompanyTerritoryType(uint grandCompany) => grandCompany switch
        {
            1 => 128u,
            2 => 132u,
            _ => 130u
        };

        internal static unsafe uint GetGrandCompany() => UIState.Instance()->PlayerState.GrandCompany;

        internal static unsafe uint GetGrandCompanyRank() => UIState.Instance()->PlayerState.GetGrandCompanyRank();

        internal static uint GetMaxDesynthLevel() => Svc.Data.Excel.GetSheet<Item>().Where(x => x.Desynth > 0).OrderBy(x => x.LevelItem.RowId).LastOrDefault().LevelItem.RowId;

        internal static unsafe float GetDesynthLevel(uint classJobId) => PlayerState.Instance()->GetDesynthesisLevel(classJobId);

        /// <summary>
        /// 取得指定職業(省略時為目前職業)的等級。查無此列、或該職業沒有經驗值欄位時回 0(未知)。
        /// </summary>
        /// <remarks>
        /// 🔴 <c>ExpArrayIndex</c> 是 <c>sbyte</c>,而<b>第 0 列(冒險者/ADV)是 -1</b>
        /// (2026-09-07 離線確認台服 7.20 的 ClassJob 表 46 列中只有第 0 列是負的,最大值 31)。
        /// <c>ClassJobLevels</c> 是 <c>FixedSizeArray35</c>,索引 -1 會擲
        /// <see cref="IndexOutOfRangeException"/>。舊寫法的 <c>?? 0</c> 只擋「查無此列」,
        /// <b>擋不到「列存在但 ExpArrayIndex 是 -1」</b>。
        /// <br/><br/>
        /// 🔴 這個函式的呼叫點裡,<c>ConfigTab.DrawPlannerUi</c> 與 <c>MainTab</c> 都在 ImGui 繪製
        /// 路徑上 —— 擲一次例外整個主視窗就不畫,而 Dalamud 在 10 秒內收到兩次 <c>Draw()</c> 例外
        /// 會<b>永久停用該視窗</b>,外掛端清不掉,使用者看到的是「主視窗突然消失」。
        /// (實機 2026-08-31 12:33:50.519 與 12:33:54.549 相隔 4.03 秒各擲一次,已滿足該條件。)
        /// <br/><br/>
        /// 📌 ADV(0) <b>不是罕見暫態</b>:<see cref="GetJob"/> 在 <c>Player.Available</c> 為 false 時
        /// 回 <c>Plugin.JobLastKnown</c>,而該欄位唯一的寫入點 <c>GetJobAndLevelingCheck</c> 被
        /// <c>Stage != Stopped</c>、<c>Player.Available</c>、<c>CurrentTerritoryContent != null</c>
        /// 三重閘門擋著 ⇒ <b>從未按下開始的使用者,整個 session 都停在 <c>default(Job)</c> == ADV</b>,
        /// 於是每一次區域切換(<c>Player.Available</c> 轉 false)都會踩到。
        /// <br/><br/>
        /// 📌 退路回 0 而不是索引 0:經驗值欄位第 0 格是格鬥士/武僧(PGL/MNK),
        /// 拿它當未知職業的等級是<b>安靜的錯答案</b>。
        /// </remarks>
        internal static unsafe short GetCurrentLevelFromSheet(Job? job = null)
        {
            Job resolvedJob = job ?? GetJob();

            var classJobRow = Svc.Data.GetExcelSheet<ClassJob>().GetRowOrDefault((uint)resolvedJob);
            if (classJobRow == null)
            {
                LogMissingExpArrayIndexOnce(resolvedJob, "查無此列");
                return 0;
            }

            var expArrayIndex = classJobRow.Value.ExpArrayIndex;
            if (expArrayIndex < 0)
            {
                LogMissingExpArrayIndexOnce(resolvedJob, $"ExpArrayIndex 為 {expArrayIndex}(該職業沒有經驗值欄位)");
                return 0;
            }

            PlayerState* playerState = PlayerState.Instance();
            return playerState->ClassJobLevels[expArrayIndex];
        }

        private static readonly HashSet<uint> LoggedJobsWithoutExpArrayIndex = new();

        /// <summary>
        /// 同一個 ClassJob id 只寫一行 <c>Information</c>,之後靜默。
        /// </summary>
        /// <remarks>
        /// 🔴 用鎖而不是裸 <see cref="HashSet{T}"/>:<see cref="GetCurrentLevelFromSheet"/> 的呼叫點
        /// 同時有 ImGui 繪製路徑與 <c>InventoryHelper</c>/<c>ContentHelper</c>,並行插入的失敗形式
        /// 是集合本身壞掉,不是「拿到舊值」。只有失敗路徑會進到這裡,鎖是無競爭的。
        /// 🔴 <c>Svc.Log</c> 的呼叫刻意放在鎖外(鎖內不做 I/O)。
        /// 用 <c>Information</c> 而不是 <c>DuoLog</c>:後者每個等級都會無條件洗使用者的聊天視窗。
        /// </remarks>
        private static void LogMissingExpArrayIndexOnce(Job job, string reason)
        {
            lock (LoggedJobsWithoutExpArrayIndex)
            {
                if (!LoggedJobsWithoutExpArrayIndex.Add((uint)job))
                    return;
            }

            Svc.Log.Information($"[PlayerHelper] ClassJob {(uint)job} ({job}) {reason},等級以 0 回報。");
        }

        internal static float JobRange
        {
            get
            {
                float radius = 25;
                if (!Player.Available) 
                    return radius;
                radius = (Svc.Data.GetExcelSheet<ClassJob>().GetRowOrDefault(Player.Object.ClassJob.RowId)?.GetJobRole() ?? JobRole.None) switch
                {
                    JobRole.Tank or JobRole.Melee => 2.6f,
                    _ => radius
                };
                return radius;
            }
        }

        internal static float AoEJobRange
        {
            get
            {
                float radius = 10;
                if (!Player.Available) return radius;
                radius = (Svc.Data.GetExcelSheet<ClassJob>().GetRowOrDefault(Player.Object.ClassJob.RowId)?.GetJobRole() ?? JobRole.None) switch
                {
                    JobRole.Tank or JobRole.Melee => 2.6f,
                    _ => radius
                };

                if (Player.Object.ClassJob.RowId == 38)
                    radius = 3;
                return radius;
            }
        }

        internal static JobRole GetJobRole(this ClassJob job)
        {
            var role = (JobRole)job.Role;

            if (role is JobRole.Ranged or JobRole.None)
            {
                role = job.ClassJobCategory.RowId switch
                {
                    30 => JobRole.Ranged_Physical,
                    31 => JobRole.Ranged_Magical,
                    32 => JobRole.Disciple_Of_The_Land,
                    33 => JobRole.Disciple_Of_The_Hand,
                    _ => JobRole.None,
                };
            }
            return role;
        }

        internal static unsafe bool IsValid =>
            Control.GetLocalPlayer() != null
         && ThreadSafety.IsMainThread
         && Svc.Condition.Any()
         && !Svc.Condition[ConditionFlag.BetweenAreas]
         && !Svc.Condition[ConditionFlag.BetweenAreas51]
         && Player.Available
         && Player.Interactable;

        internal static bool IsJumping => Svc.Condition.Any()
        && (Svc.Condition[ConditionFlag.Jumping]
        || Svc.Condition[ConditionFlag.Jumping61]);

        internal static unsafe bool IsAnimationLocked => ActionManager.Instance()->AnimationLock > 0;

        internal static bool IsReady => IsValid && !IsOccupied;

        internal static bool IsOccupied => GenericHelpers.IsOccupied() || Svc.Condition[ConditionFlag.Jumping61];

        internal static bool IsReadyFull => IsValid && !IsOccupiedFull;

        internal static bool IsOccupiedFull => IsOccupied || IsAnimationLocked;

        internal static unsafe bool IsCasting => Player.Character->IsCasting;

        // 🔴 AgentMap.Instance() 合法回 null(產生器本體即 agentModule == null ? null : ...),
        //    裸解參考 = AccessViolationException,corrupted-state,try/catch 攔不到。
        // fail-closed:讀不到就回 false。兩個呼叫端(AutoDuty.cs:1477、MovementHelper.cs:61)
        //    都是拿它當「要不要額外放衝刺/馬車」的觸發條件,false ＝ 不放技能,
        //    正是讀不到狀態時該有的行為(回 true 反而會在未知狀態下送出動作)。
        internal static unsafe bool IsMoving
        {
            get
            {
                AgentMap* agentMap = AgentMap.Instance();
                return agentMap != null && agentMap->IsPlayerMoving;
            }
        }

        internal static unsafe bool InCombat => Svc.Condition[ConditionFlag.InCombat];

        /*internal static unsafe short GetCurrentItemLevelFromGearSet(int gearsetId = -1, bool updateGearsetBeforeCheck = true)
        {
            RaptureGearsetModule* gearsetModule = RaptureGearsetModule.Instance();
            if (gearsetId < 0)
                gearsetId = gearsetModule->CurrentGearsetIndex;
            if (updateGearsetBeforeCheck)
                gearsetModule->UpdateGearset(gearsetId);
            return gearsetModule->GetGearset(gearsetId)->ItemLevel;
        }*/

        internal static Job GetJob() => Player.Available ? Player.Job : Plugin.JobLastKnown;

        internal static CombatRole GetCombatRole(this Job? job) => 
            job != null ? GetCombatRole((Job)job) : CombatRole.NonCombat;

        internal static CombatRole GetCombatRole(this Job job)
        {
            return job switch
            {
                Job.GLA or Job.PLD or Job.MRD or Job.WAR or Job.DRK or Job.GNB => CombatRole.Tank,
                Job.CNJ or Job.WHM or Job.SGE or Job.SCH or Job.AST => CombatRole.Healer,
                Job.PGL or Job.MNK or Job.LNC or Job.DRG or Job.ROG or Job.NIN or Job.SAM or Job.RPR or Job.VPR or 
                    Job.ARC or Job.BRD or Job.DNC or Job.MCH or
                    Job.THM or Job.BLM or Job.ACN or Job.SMN or Job.RDM or Job.PCT or Job.BLU => CombatRole.DPS,
                _ => CombatRole.NonCombat,
            };
        }

        internal static bool HasStatus(uint statusID, float minTime = 0) => Svc.Objects.LocalPlayer != null && Player.Object.StatusList.Any(x => x.StatusId == statusID && (minTime <= 0 || x.RemainingTime > minTime));
    }
}
