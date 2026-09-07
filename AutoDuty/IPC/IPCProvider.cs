using AutoDuty.Helpers;
using ECommons.DalamudServices;
using ECommons.EzIpcManager;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
#nullable disable

namespace AutoDuty.IPC
{
    internal class IPCProvider
    {
        internal IPCProvider()
        {
            EzIPC.Init(this);
        }

        /// <summary>
        /// 從別的執行緒進來的端點工作,照先進先出排在這裡等 framework 執行緒來排乾。
        /// </summary>
        /// <remarks>
        /// 🔴 刻意<b>不</b>用 <c>Svc.Framework.RunOnFrameworkThread</c> 逐則排隊:本 pin 的
        /// <c>ThreadBoundTaskScheduler</c> 把待跑的工作放在 <c>ConcurrentDictionary</c> 裡、
        /// <c>Run()</c> 走訪的是 <c>Keys</c>(Dalamud/Utility/ThreadBoundTaskScheduler.cs)——
        /// <b>同一格內不保證先進先出</b>。而這幾個端點的順序是有語意的:呼叫端連著打
        /// <c>Stop()</c> 再 <c>Run()</c>,順序一倒過來就變成「先開始跑、再把它整個停掉」。
        /// </remarks>
        private static readonly ConcurrentQueue<(string Name, Action Action)> PendingWork = new();

        /// <summary>
        /// 把端點的實際工作放到 framework 執行緒上跑。
        /// </summary>
        /// <remarks>
        /// 🔴 IPC 端點跑在<b>呼叫端的執行緒</b>上。<see cref="Run"/>／<see cref="Start"/>／
        /// <see cref="Stop"/> 這三支是「開始/停止跑副本」的指令型端點,底下會走到:
        /// <c>Player.Available</c>／<c>Player.Object.ClassJob</c>(<c>IObjectTable</c> 的包裝是
        /// 每格重用、Address 就地改寫的,從別的執行緒讀等於對隨時可能被換掉的原生指標解參考)、
        /// <c>TaskManager</c> 的 Enqueue/Abort(framework 執行緒同時在走訪那兩個裸 List)、
        /// 路徑檔的讀取、對 vnavmesh/BossMod/PandorasBox 的 IPC、以及 ImGui 視窗的開關。
        /// 這些沒有一項可以在別人的執行緒上做,而 AccessViolation 在 .NET Core 是
        /// corrupted-state exception,try/catch 攔不到。
        /// <br/><br/>
        /// 已經在 framework 執行緒時<b>就地執行</b>:例外照樣往呼叫端擲,回傳值與時序與改動前
        /// 完全相同(Questionable 從自己的 framework tick 打進來的呼叫走這條)。
        /// 在別的執行緒時排進佇列,下一次 Framework_Update 開頭排乾且<b>不等待</b> ——
        /// 這三支回傳型別都是 <c>void</c>,「不等待」不改變任何回傳語意;不等待也避免了
        /// 「呼叫端持著鎖同步等 framework 執行緒」這種死結形狀。
        /// </remarks>
        private static void RunOnFramework(string endpointName, Action action)
        {
            if (Svc.Framework.IsInFrameworkUpdateThread)
            {
                action();
                return;
            }
            PendingWork.Enqueue((endpointName, action));
        }

        /// <summary>
        /// 由 <c>AutoDuty.Framework_Update</c> 每幀在最前面呼叫一次,把 <see cref="PendingWork"/> 排乾。
        /// 每一則各自包 try:其中一則擲例外不會讓後面的排不出去,也不會中斷這一格的其餘處理。
        /// </summary>
        internal static void DrainPendingWork()
        {
            while (PendingWork.TryDequeue(out var work))
            {
                try
                {
                    work.Action();
                }
                catch (Exception e)
                {
                    Svc.Log.Error($"AutoDuty IPC {work.Name} 在 framework 執行緒上執行失敗:{e}");
                }
            }
        }

        /// <summary>把所有設定名稱與目前的值印進 log。</summary>
        /// <remarks>
        /// 📌 這支<b>刻意不排進</b> <see cref="PendingWork"/>:它只做反射<b>讀取</b>
        /// (<c>FieldInfo.GetValue</c>)與 <c>Svc.Log.Info</c>,不寫任何欄位、不存檔、不碰原生資料,
        /// 而 Serilog 的寫入端本身是執行緒安全的。排進佇列只會讓輸出晚一格,換不到任何安全性。
        /// </remarks>
        [EzIPC] public void ListConfig() => ConfigHelper.ListConfig();

        /// <summary>讀一個設定目前的值。</summary>
        /// <remarks>
        /// 📌 同樣<b>刻意不排隊</b>:它要<b>同步回傳字串</b>,排隊就得同步等 framework 執行緒,
        /// 而呼叫端很可能是在自己的鎖裡問這個值 —— 那正是死結形狀。它只做反射讀取,
        /// 最壞情況是讀到上一格的值,而跨外掛的呼叫端本來就不能假設兩邊的時序。
        /// ⚠️ 已知殘留風險:<c>FieldInfo.GetValue</c> 對「大於一個機器字的實值型別欄位」
        /// 在別的執行緒同時寫入時可能讀到撕裂值。AutoDuty 的設定欄位目前是
        /// bool／int／float／enum／string／清單,沒有這種欄位,所以這裡不動。
        /// </remarks>
        [EzIPC] public string GetConfig(string config) => ConfigHelper.GetConfig(config);

        /// <summary>改一個設定的值並存檔。</summary>
        /// <remarks>
        /// 🔴 改動前這支在<b>呼叫端的執行緒</b>上做了兩件不可以在那裡做的事:
        /// <c>FieldInfo.SetValue(Plugin.Configuration, ...)</c>(framework 執行緒每幀在讀那些欄位)
        /// 與 <c>Plugin.Configuration.Save()</c> —— 後者就是 <c>EzConfig.Save()</c>,
        /// 整份序列化<b>加寫檔</b>,而序列化路徑還會走進 <c>ConfigOverrideHelper.WithUserValues</c> 的鎖。
        /// 現在排進 <see cref="PendingWork"/>。
        /// <br/><br/>
        /// 🔑 <b>和 <see cref="Run"/>／<see cref="Start"/>／<see cref="Stop"/> 共用同一條佇列是必要的</b>:
        /// Questionable 的 <c>AutoDutyIpc.StartInstance</c> 先打 <c>SetConfig</c> 再打 <c>Run</c>,
        /// 同一條 FIFO 佇列才保得住這個先後順序;拆成兩條佇列(或其中一支改用
        /// <c>RunOnFrameworkThread</c>,它同一格內不保證先進先出)就會變成「先開始跑、設定才生效」。
        /// <br/><br/>
        /// ⚠️ 回傳型別是 <c>void</c>,「排隊不等待」不改變任何回傳語意。但呼叫端若在
        /// <c>SetConfig</c> 之後<b>立刻</b>用 <see cref="GetConfig"/> 回讀,會讀到舊值(最多晚一格)。
        /// </remarks>
        [EzIPC] public void SetConfig(string config, string setting) => RunOnFramework(nameof(SetConfig), () => ConfigHelper.ModifyConfig(config, setting));

        /// <summary>
        /// 暫時覆寫一組設定:只改執行期的值,存檔時寫回使用者原本的值,<see cref="PopConfigOverrides"/>
        /// 或 AutoDuty 停止時還原。任何一項驗不過就整批不套用並回 <c>false</c>。
        /// </summary>
        /// <remarks>
        /// ⚠️ 這支跑在<b>呼叫端的執行緒</b>上。CallGate 對型別不同的參數會做一次 JSON 來回轉換,
        /// 所以 <c>Dictionary&lt;string, string&gt;</c> 到這裡可能已經變成 <c>JObject</c> —— 兩種都收。
        /// </remarks>
        [EzIPC]
        public bool PushConfigOverrides(object overrides)
        {
            Dictionary<string, string> dict;
            try
            {
                dict = overrides switch
                       {
                           Dictionary<string, string> d => d,
                           JObject jo                   => jo.ToObject<Dictionary<string, string>>(),
                           _                            => null
                       };
            }
            catch (Exception ex)
            {
                Svc.Log.Error($"AutoDuty 設定覆寫:參數轉不成 Dictionary<string, string>:{ex.Message}");
                return false;
            }

            if (dict == null)
            {
                Svc.Log.Error($"AutoDuty 設定覆寫:參數要是 Dictionary<string, string>,收到的是 {overrides?.GetType().FullName ?? "null"}。");
                return false;
            }

            return ConfigOverrideHelper.Push(dict);
        }

        /// <summary>還原所有設定覆寫。AutoDuty 停止時本來就會自己做一次,呼叫端不一定要用。</summary>
        [EzIPC] public bool PopConfigOverrides() => ConfigOverrideHelper.Pop();

        [EzIPC]
        public void Run(uint territoryType, int loops = 0, bool bareMode = false)
        {
            RunOnFramework(nameof(Run), () =>
            {
                var ctx = Plugin.BuildCommandRunContext(territoryType, loops, startFromZero: true, bareMode: bareMode, source: RunSource.IPC, persistLoopsToConfig: true);
                if (ctx != null)
                    Plugin.Run(ctx);
                else
                    Plugin.Run(territoryType, loops, startFromZero: true, bareMode: bareMode);
            });
        }
        [EzIPC] public void Start(bool startFromZero = true) => RunOnFramework(nameof(Start), () => Plugin.StartNavigation(startFromZero));
        [EzIPC] public void Stop() => RunOnFramework(nameof(Stop), () => Plugin.Stage = Stage.Stopped);
        [EzIPC] public bool IsNavigating() => Plugin.States.HasFlag(PluginState.Navigating);
        [EzIPC] public bool IsLooping() => Plugin.States.HasFlag(PluginState.Looping);
        [EzIPC] public bool IsStopped() => Plugin.Stage == Stage.Stopped;
        /// <summary>某個領土有沒有路徑檔。</summary>
        /// <remarks>
        /// 📌 <b>不需要排隊</b>:<c>ContentPathsManager.DictionaryPaths</c> 是<b>整份替換</b>發布的
        /// (<c>Updater/FileHelper.Update</c> 先在區域變數裡建好新字典,再由 framework 執行緒一次指派),
        /// 發布之後那份字典不再被就地改動 —— <c>RemoveInvalidPaths</c> 動的是容器裡的
        /// <c>Paths</c> 清單,不是字典本身。這一行的 <c>ContainsKey</c> 只讀一次靜態欄位,
        /// 拿到的必定是某一份已經建好的完整字典;該欄位另標了 <c>volatile</c> 保證發布順序。
        /// </remarks>
        [EzIPC] public bool ContentHasPath(uint territoryType) => ContentPathsManager.DictionaryPaths.ContainsKey(territoryType);

        //Callback for Wrath Combo Lease Cancel
        /// <summary>Wrath Combo 取消租約時回呼進來。</summary>
        /// <remarks>
        /// 🔴 改動前這支在 <b>Wrath Combo 的執行緒</b>上直接改
        /// <c>Plugin.Configuration.AutoManageRotationPluginState</c>、呼叫
        /// <c>Plugin.Configuration.Save()</c>(整份序列化加寫檔),並把 <c>Wrath_IPCSubscriber</c>
        /// 的租約欄位清成 null —— 這三樣 framework 執行緒都在讀。現在排進 <see cref="PendingWork"/>,
        /// 最多晚一格生效。晚一格不會多送出旋轉指令:租約在對方那端已經作廢,
        /// 這一格就算再打一次 IPC 也只會被拒絕。
        /// </remarks>
        [EzIPC] public void WrathComboCallback(int reason, string s) => RunOnFramework(nameof(WrathComboCallback), () => Wrath_IPCSubscriber.CancelActions(reason, s));
    }
}
