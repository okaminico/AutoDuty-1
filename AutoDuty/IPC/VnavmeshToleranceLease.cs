using ECommons.DalamudServices;
using System;

namespace AutoDuty.IPC
{
    /// <summary>
    /// vnavmesh 路徑容許值（<c>FollowPath.Tolerance</c>）的<b>租約</b>持有者。
    /// 提供端給不了租約時，逐字退回舊的 <c>Path.SetTolerance</c> 直接寫入。
    /// </summary>
    /// <remarks>
    /// 🔴🔴 <b>改用租約的理由＝舊的開關沒有主人。</b><c>Path.SetTolerance</c> 寫的是 vnavmesh
    /// 執行期的<b>單一格全域</b> float，而 Questionable 也寫同一格（每次尋路寫 0.25）。
    /// 兩邊都是單向寫入、都沒有還原 ⇒ <b>最後寫入者獲勝並永久停在那裡</b>，全程零訊息：
    /// AutoDuty 為了「走到定點前放寬判定」寫的 4.0 被別人蓋成 0.25，或反過來把別人的值蓋掉。
    /// <para>
    /// 🔑 租約是<b>記名</b>的疊加層：提供端讀取時一律是「租約值 ?? 使用者的值」，
    /// 我們只押著自己那一把，放約或逾時就自動還原，<b>使用者那格欄位一個字都不會被動到</b>。
    /// </para>
    /// <para>
    /// 🔴 <b>永久持有比改動前更霸道</b>：押著的期間別人的 <c>Path.SetTolerance</c> 完全不生效
    /// （使用者的值被我們的租約值蓋過去）。所以一定要有放約點 ——
    /// <see cref="ReleaseAndRestore"/>（AutoDuty 停止）與 <see cref="Tick"/> 的閒置放約。
    /// </para>
    /// <para>
    /// ⚠️ <b>執行緒</b>：<see cref="Request"/> 與 <see cref="Tick"/> 走 Framework 執行緒，
    /// 而 <see cref="ReleaseAndRestore"/> 可以從 UI 按鈕（繪製執行緒）進來 ⇒ 狀態全程上鎖。
    /// 🔴 <b>鎖內只碰欄位</b>：絕不呼叫 IPC、絕不寫 log、絕不碰 ImGui、絕不做檔案 I/O。
    /// 每一段都是「鎖內拍快照 → 出鎖做事 → 需要時再進鎖記錄結果」。
    /// 🔴 <b>絕不使用 ECommons 的 EzThrottler</b> —— 它是整個外掛共用的靜態 <c>Dictionary</c>
    /// 且零同步，從非 Framework 執行緒碰它的失敗形式是<b>字典本身壞掉</b>，
    /// 還會連帶弄壞同一外掛內所有模組的節流。這裡的節流一律自己算 <see cref="Environment.TickCount64"/>。
    /// </para>
    /// </remarks>
    internal static class VnavmeshToleranceLease
    {
        /// <summary>租約登記的名字，會出現在 vnavmesh 的 log 與租約清單裡。</summary>
        private const string LeaseOwner = "AutoDuty";

        /// <summary>每次取得／續約要求的租期（5 分鐘）＝提供端的硬性上限。</summary>
        /// <remarks>
        /// 🔴 這個值<b>不可以</b>大於提供端的上限：提供端是<b>夾值不是拒絕</b>，
        /// 要多了只會被靜默砍短，續約反而會來不及。
        /// </remarks>
        private const int LeaseMilliseconds = 300_000;

        /// <summary>續約間隔（30 秒），是 <see cref="LeaseMilliseconds"/> 的十分之一。</summary>
        /// <remarks>
        /// ⚠️ 提供端 <c>Renew</c> 的第一件事是掃除到期租約 ⇒ 續約間隔只要接近租期，
        /// 第一次心跳送到時那把已經被掃掉、續約<b>必定</b>回 false（不是競態，是每次都會發生）。
        /// 留 10 倍餘裕：要連續漏掉 9 次心跳才會真的過期。
        /// </remarks>
        private const int RenewIntervalMilliseconds = 30_000;

        /// <summary>閒置多久之後放約（60 秒）。</summary>
        /// <remarks>
        /// 🔴 <b>只靠「多久沒有寫入」判閒置是錯的。</b><c>MovementHelper.Move</c> 在
        /// <c>Path_NumWaypoints() &gt; 0</c> 時就提早 return 了 ⇒ <b>長距離移動的整段期間
        /// 一次容許值寫入都不會發生</b>，純計時會在路徑跑到一半放約、容許值當場跳回使用者的值。
        /// 所以 <see cref="Tick"/> 在真的放約之前一定會再問一次 <c>Path.IsRunning</c>。
        /// </remarks>
        private const int IdleGraceMilliseconds = 60_000;

        /// <summary>取不到租約之後，隔多久才重新探測一次（60 秒）。</summary>
        /// <remarks>
        /// 🔴 沒有這個冷卻的話，vnavmesh 太舊時<b>每一幀</b>都會對一個不存在的端點送一次 IPC，
        /// 而每一次都會走 <c>EzIpcFailureLog</c> 的例外路徑。
        /// </remarks>
        private const int ProbeIntervalMilliseconds = 60_000;

        /// <summary>舊路徑還原時寫回的值。<b>改動前的常數，逐字保留。</b></summary>
        private const float LegacyRestoreTolerance = 0.25f;

        /// <summary>只保護下面那幾個欄位。<b>鎖內不做任何 IPC／log／I/O。</b></summary>
        private static readonly object Gate = new();

        /// <summary>目前持有的租約；<see cref="Guid.Empty"/>＝沒有。</summary>
        private static Guid _lease;

        /// <summary><see cref="Environment.TickCount64"/> 座標系的下次續約時刻。</summary>
        private static long _nextRenewAt;

        /// <summary>閒置放約的期限（每次 <see cref="Request"/> 都會往後推）。</summary>
        private static long _idleDeadline;

        /// <summary>下次可以重新探測租約端點的時刻。</summary>
        private static long _nextProbeAt;

        /// <summary>最後一次要求的容許值（續約失敗後重新取得時要把它押回去）。</summary>
        private static float _desired = LegacyRestoreTolerance;

        /// <summary>
        /// 我們有沒有<b>真的寫過</b>使用者那格全域欄位（走了舊路徑）。
        /// </summary>
        /// <remarks>
        /// 🔴 這個旗標決定停止時<b>要不要做舊路徑的還原</b>。沒寫過就不要碰使用者的欄位 ——
        /// 改動前那段無條件的還原（<c>GetTolerance() &gt; 0.25 就寫回 0.25</c>）在租約模式下
        /// 會變成<b>新的越權</b>：<c>Path.GetTolerance</c> 回的是「實際生效的值」（租約值 ?? 使用者的值），
        /// 讀到的可能是我們自己、甚至是<b>別的外掛</b>押著的租約值，
        /// 而寫回去的目標卻是<b>使用者那格欄位</b> ⇒ 等於拿別人的租約值去改使用者的設定。
        /// </remarks>
        private static bool _legacyEngaged;

        /// <summary>退回舊路徑的說明只寫一次，避免每幀洗 log。</summary>
        private static bool _legacyLogged;

        /// <summary>
        /// 請求把 vnavmesh 的路徑容許值押成 <paramref name="tolerance"/>。
        /// 取得得到租約就走租約，否則逐字退回舊的 <c>Path.SetTolerance</c>。
        /// </summary>
        internal static void Request(float tolerance)
        {
            if (!VNavmesh_IPCSubscriber.IsEnabled)
                return;

            // 🔴 提供端明確拒絕 NaN／無限大（那會讓整條路徑在同一幀被消耗光，而且完全沒有訊息）。
            //    在這裡先擋掉，免得「套不上」被誤判成「租約已經不在了」而觸發取得／失敗的高頻迴圈。
            if (!float.IsFinite(tolerance))
            {
                Svc.Log.Information($"[AutoDuty] 忽略一個不是有限數的路徑容許值要求（{tolerance}）。");
                return;
            }

            var now = Environment.TickCount64;

            Guid lease;
            bool mayProbe;
            lock (Gate)
            {
                _desired      = tolerance;
                _idleDeadline = now + IdleGraceMilliseconds;
                lease         = _lease;
                mayProbe      = lease == Guid.Empty && now >= _nextProbeAt;
            }

            if (lease == Guid.Empty)
            {
                if (!mayProbe)
                {
                    // 還在重探冷卻期內：不再對一個（很可能）不存在的端點送 IPC。
                    WriteLegacy(tolerance);
                    return;
                }

                lease = TryAcquire(now);
                if (lease == Guid.Empty)
                {
                    WriteLegacy(tolerance);
                    return;
                }

                if (ApplyLeasedTolerance(lease, tolerance))
                    return;

                // 剛拿到就套不上（幾乎不可能）。🔴 這裡刻意不重設重探冷卻，
                // 否則會變成「取得 → 套不上 → 立刻再取得」的每幀迴圈，把 vnavmesh 的
                // 租約上限（32 把）吃光並洗爆使用者的 log。
                DropStaleLease(lease, resetProbeCooldown: false);
                WriteLegacy(tolerance);
                return;
            }

            if (ApplyLeasedTolerance(lease, tolerance))
                return;

            // 手上這把在提供端已經不在了（逾時被掃掉，或 vnavmesh 重載過）。
            // 丟掉並允許下一次 Request 立刻重新取得。
            DropStaleLease(lease, resetProbeCooldown: true);
            WriteLegacy(tolerance);
        }

        /// <summary>
        /// 續約心跳＋閒置放約。由 <c>AutoDuty.Framework_Update</c> 每幀呼叫，內部自行節流；
        /// 沒有租約時是「一個欄位判斷就返回」。
        /// </summary>
        internal static void Tick()
        {
            var now = Environment.TickCount64;

            Guid lease;
            bool idle;
            bool renew;
            lock (Gate)
            {
                lease = _lease;
                if (lease == Guid.Empty)
                    return;

                idle  = now >= _idleDeadline;
                renew = !idle && now >= _nextRenewAt;
                if (renew)
                    _nextRenewAt = now + RenewIntervalMilliseconds;
            }

            if (idle)
            {
                // 🔴 閒置期限到了，但路徑可能還在跑：長距離移動期間一次容許值寫入都不會發生
                //    （MovementHelper.Move 在 Path_NumWaypoints() > 0 時提早 return），
                //    這時放約會讓容許值在路徑跑到一半跳回使用者的值。所以放約前先問一次。
                //    ⚠️ 這一支 IPC 最多每 IdleGraceMilliseconds 才會被走到一次，不是每幀。
                if (PathIsRunning())
                {
                    lock (Gate)
                    {
                        if (_lease == lease)
                            _idleDeadline = now + IdleGraceMilliseconds;
                    }

                    return;
                }

                ReleaseLeaseOnly("閒置逾時");
                return;
            }

            if (!renew)
                return;

            if (RenewLease(lease))
                return;

            // 續約回 false ＝那把已經不在了，必須重新取得，不能繼續假設自己還押著。
            Svc.Log.Information($"[AutoDuty] vnavmesh 路徑容許值租約 {lease} 已經不在了，重新取得一把。");

            float desired;
            lock (Gate)
            {
                if (_lease == lease)
                    _lease = Guid.Empty;
                desired      = _desired;
                _nextProbeAt = 0;
            }

            var fresh = TryAcquire(now);
            if (fresh == Guid.Empty)
            {
                Svc.Log.Information("[AutoDuty] 重新取得 vnavmesh 路徑容許值租約失敗，退回舊的 Path.SetTolerance 寫入。");
                WriteLegacy(desired);
                return;
            }

            if (ApplyLeasedTolerance(fresh, desired))
                return;

            DropStaleLease(fresh, resetProbeCooldown: false);
            WriteLegacy(desired);
        }

        /// <summary>
        /// AutoDuty 停下來時呼叫：放開租約，並且<b>只有在真的寫過使用者那格欄位時</b>
        /// 才做舊路徑的還原。冪等。
        /// </summary>
        internal static void ReleaseAndRestore()
        {
            ReleaseLeaseOnly("AutoDuty 停止");

            bool legacy;
            lock (Gate)
            {
                legacy         = _legacyEngaged;
                _legacyEngaged = false;
                _desired       = LegacyRestoreTolerance;
                _nextProbeAt   = 0;
            }

            if (!legacy)
                return;

            // ── 改動前的還原邏輯，逐字保留（只在我們真的寫過使用者那格欄位時才走到）──
            if (VNavmesh_IPCSubscriber.IsEnabled && VNavmesh_IPCSubscriber.Path_GetTolerance() > LegacyRestoreTolerance)
                VNavmesh_IPCSubscriber.Path_SetTolerance(LegacyRestoreTolerance);
        }

        /// <summary>取一把租約。回 <see cref="Guid.Empty"/>＝提供端給不了（沒裝／太舊／已達上限）。</summary>
        private static Guid TryAcquire(long now)
        {
            Guid lease;
            try
            {
                lease = VNavmeshExtraIPC.Path_AcquireSuppressionFor(LeaseOwner, LeaseMilliseconds);
            }
            catch
            {
                // SafeWrapper 只吃 IpcNotReadyError（吃掉之後回 default ＝ Guid.Empty）；
                // 別的例外（例如 IpcTypeMismatchError）不能讓它打斷 Framework.Update。
                lease = Guid.Empty;
            }

            if (lease == Guid.Empty)
            {
                lock (Gate)
                    _nextProbeAt = now + ProbeIntervalMilliseconds;
                return Guid.Empty;
            }

            lock (Gate)
            {
                _lease        = lease;
                _nextRenewAt  = now + RenewIntervalMilliseconds;
                _idleDeadline = now + IdleGraceMilliseconds;
            }

            Svc.Log.Information($"[AutoDuty] 已向 vnavmesh 取得路徑容許值租約 {lease}（{LeaseMilliseconds} 毫秒）。");
            return lease;
        }

        /// <summary>把值押到租約上。回 <see langword="false"/>＝這把租約在提供端已經不在了。</summary>
        private static bool ApplyLeasedTolerance(Guid lease, float tolerance)
        {
            try
            {
                return VNavmeshExtraIPC.Path_SetLeasedTolerance(lease, tolerance);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>續約（心跳）。回 <see langword="false"/>＝那把已經不在了。</summary>
        private static bool RenewLease(Guid lease)
        {
            try
            {
                return VNavmeshExtraIPC.Path_RenewSuppressionFor(lease, LeaseMilliseconds);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>手上這把已經是廢的：丟掉本地狀態，不對提供端送 Release。</summary>
        private static void DropStaleLease(Guid lease, bool resetProbeCooldown)
        {
            lock (Gate)
            {
                if (_lease == lease)
                    _lease = Guid.Empty;
                if (resetProbeCooldown)
                    _nextProbeAt = 0;
            }
        }

        /// <summary>交回租約（沒有就什麼都不做）。<b>不碰使用者那格欄位。</b>冪等。</summary>
        private static void ReleaseLeaseOnly(string reason)
        {
            Guid lease;
            lock (Gate)
            {
                lease = _lease;

                // 🔴 先清欄位再送出：送出途中擲例外的話，手上這把也已經是廢的了。
                _lease        = Guid.Empty;
                _idleDeadline = 0;
                _nextRenewAt  = 0;
            }

            if (lease == Guid.Empty)
                return;

            try
            {
                VNavmeshExtraIPC.Path_ReleaseSuppression(lease);
            }
            catch
            {
                // 交不回去也不要緊：提供端會讓它自行逾時（上限 5 分鐘）並寫一行 Information。
            }

            Svc.Log.Information($"[AutoDuty] 已交回 vnavmesh 路徑容許值租約 {lease}（{reason}）。");
        }

        /// <summary>vnavmesh 現在還有沒有在跑路徑。任何失敗一律當成「還在跑」（不放約）。</summary>
        /// <remarks>
        /// 🔑 失敗方向刻意選「保守」：誤判成沒在跑會讓容許值在路徑中途跳回使用者的值，
        /// 而誤判成還在跑最多只是晚一個 <see cref="IdleGraceMilliseconds"/> 才放約
        /// —— 而且提供端本來就有 5 分鐘的硬性逾時兜底。
        /// </remarks>
        private static bool PathIsRunning()
        {
            try
            {
                return VNavmesh_IPCSubscriber.Path_IsRunning();
            }
            catch
            {
                return true;
            }
        }

        /// <summary>舊路徑：直接寫使用者那格全域欄位，並記下「欠一次還原」。</summary>
        private static void WriteLegacy(float tolerance)
        {
            bool first;
            lock (Gate)
            {
                first          = !_legacyLogged;
                _legacyLogged  = true;
                _legacyEngaged = true;
            }

            if (first)
                Svc.Log.Information("[AutoDuty] vnavmesh 沒有路徑容許值租約端點（沒安裝或版本太舊），" +
                                    "退回舊的 Path.SetTolerance 直接寫入。這行訊息只會出現一次。");

            VNavmesh_IPCSubscriber.Path_SetTolerance(tolerance);
        }
    }
}
