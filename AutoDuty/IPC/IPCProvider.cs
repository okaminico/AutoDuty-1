using AutoDuty.Helpers;
using ECommons.DalamudServices;
using ECommons.EzIpcManager;
using Newtonsoft.Json.Linq;
using System;
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

        [EzIPC] public void ListConfig() => ConfigHelper.ListConfig();
        [EzIPC] public string GetConfig(string config) => ConfigHelper.GetConfig(config);
        [EzIPC] public void SetConfig (string config, string setting) => ConfigHelper.ModifyConfig(config, setting);

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
            var ctx = Plugin.BuildCommandRunContext(territoryType, loops, startFromZero: true, bareMode: bareMode, source: RunSource.IPC, persistLoopsToConfig: true);
            if (ctx != null)
                Plugin.Run(ctx);
            else
                Plugin.Run(territoryType, loops, startFromZero: true, bareMode: bareMode);
        }
        [EzIPC] public void Start(bool startFromZero = true) => Plugin.StartNavigation(startFromZero);
        [EzIPC] public void Stop() => Plugin.Stage = Stage.Stopped;
        [EzIPC] public bool IsNavigating() => Plugin.States.HasFlag(PluginState.Navigating);
        [EzIPC] public bool IsLooping() => Plugin.States.HasFlag(PluginState.Looping);
        [EzIPC] public bool IsStopped() => Plugin.Stage == Stage.Stopped;
        [EzIPC] public bool ContentHasPath(uint territoryType) => ContentPathsManager.DictionaryPaths.ContainsKey(territoryType);

        //Callback for Wrath Combo Lease Cancel
        [EzIPC] public void WrathComboCallback(int reason, string s) => Wrath_IPCSubscriber.CancelActions(reason, s);
    }
}
