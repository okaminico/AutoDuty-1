using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using AutoDuty.Windows;
using ECommons.DalamudServices;

namespace AutoDuty.Helpers;

/// <summary>
/// 讓別的外掛透過 IPC 暫時覆寫 AutoDuty 的設定:只改執行期的值,存檔時寫回使用者原本的值。
/// </summary>
/// <remarks>
/// 🔴🔴 <b>這張表被三種執行緒碰</b>:setter 走 IPC 端點(跑在<b>呼叫端</b>的執行緒上)、
/// UI 每幀從繪製執行緒讀 <see cref="HasOverrides"/>、存檔時走訪整張表。
/// 裸 <c>Dictionary</c> 的失敗形式不是「拿到舊值」而是<b>字典本身壞掉</b>,而且並行改動時
/// <c>foreach</c> 會擲 <c>InvalidOperationException</c> —— 那個例外常被既有的 catch 吞成
/// 「存檔失敗」,使用者的設定就這樣沒存到。所以<b>第一版就上鎖</b>。
/// <para>
/// 🔑 鎖的紀律三條:
/// ①所有對 <see cref="Active"/> 的讀寫都在 <see cref="Gate"/> 內;
/// ②<b>鎖內絕不呼叫 ImGui、絕不做檔案 I/O</b>;
/// ③清除路徑放在 <c>finally</c>,<b>例外不可以讓覆寫永久卡住</b>
/// (上游那版 <c>Pop</c> 的 <c>foreach</c> 一旦擲例外,<c>Active</c> 就再也清不掉,
/// 該 session 剩下的存檔全部靜默失效)。
/// </para>
/// <para>
/// 📌 <b>存檔策略與上游不同</b>:上游是「有覆寫時整個不存」(<c>if (!HasOverrides) EzConfig.Save()</c>),
/// 代價是覆寫期間使用者自己改的任何設定<b>靜默不存</b>。這裡改成在<b>序列化那一刻</b>把被覆寫的
/// 欄位換回使用者的值(見 <see cref="WithUserValues{T}"/>,由
/// <c>AutoDutySerializationFactory.Serialize(object)</c> 呼叫),所以存檔照常進行、
/// 使用者改的東西照常留下,只有覆寫值不會被寫進檔案。
/// </para>
/// <para>
/// 📌 AutoDuty 的設定不是頂層鍵,而是 <c>ConfigurationMain.profileData[].Config</c>。
/// 因此每一筆覆寫都<b>連同它作用的那個 <see cref="Configuration"/> 實例一起記下來</b>;
/// 中途換設定檔(profile)時,還原會回到當初被改的那一份,不會把值寫到另一個 profile 上。
/// </para>
/// </remarks>
internal static class ConfigOverrideHelper
{
    private readonly record struct OverrideKey(FieldInfo Field, Configuration Target);

    private static readonly object                           Gate   = new();
    private static readonly Dictionary<OverrideKey, object?> Active = [];

    /// <summary>
    /// 序列化換值的重入旗標。取 <c>Plugin.Configuration</c> 在極少數情況下會自己觸發一次存檔,
    /// 那時候值已經是使用者的值了,再換一次會把覆寫值當成使用者的值記下來。
    /// </summary>
    [ThreadStatic] private static bool _inSwap;

    internal static bool HasOverrides
    {
        get
        {
            lock (Gate)
                return Active.Count > 0;
        }
    }

    /// <summary>
    /// 套用一組設定覆寫。任何一項驗不過就<b>整批不套用</b>(全部驗完才動手,所以不存在
    /// 「套了一半再回退」的時間窗)。
    /// </summary>
    internal static bool Push(Dictionary<string, string> overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            Svc.Log.Error("AutoDuty 設定覆寫:傳進來的字典是空的。");
            return false;
        }

        // ── 第一階段:全部在鎖外驗完並轉好型別。這裡不碰 Active,也不改任何設定。
        List<(FieldInfo Field, object? Value)> resolved = new(overrides.Count);
        foreach ((string name, string value) in overrides)
        {
            FieldInfo? field = ConfigHelper.FindConfig(name);
            if (field is null)
            {
                Svc.Log.Error($"AutoDuty 設定覆寫:找不到設定「{name}」,整批不套用。");
                return false;
            }

            if (field.FieldType.ToString().Contains("Dalamud.Plugin", StringComparison.InvariantCultureIgnoreCase))
            {
                Svc.Log.Error($"AutoDuty 設定覆寫:「{name}」是外掛內部欄位,不接受覆寫,整批不套用。");
                return false;
            }

            if (field.FieldType.IsAssignableTo(typeof(IList)))
            {
                Svc.Log.Error($"AutoDuty 設定覆寫:「{name}」是清單型設定,不支援覆寫,整批不套用。");
                return false;
            }

            object? newValue;
            string  failReason;
            try
            {
                newValue = ConfigHelper.ConvertConfigValue(field.FieldType, value, out failReason);
            }
            catch (Exception ex)
            {
                // Convert.ChangeType 對不合法的字串是擲例外不是回 null,而這裡跑在呼叫端的
                // 執行緒上 —— 讓它逃出去等於在別的外掛裡炸一個看起來與它無關的例外。
                newValue   = null;
                failReason = ex.Message;
            }

            if (newValue is null)
            {
                Svc.Log.Error($"AutoDuty 設定覆寫:「{name}」的值轉不過去({failReason}),整批不套用。");
                return false;
            }

            resolved.Add((field, newValue));
        }

        // ── 第二階段:鎖內拍快照並套用。純反射,沒有 I/O、沒有 ImGui。
        int     activeCount;
        string? error = null;
        lock (Gate)
        {
            Configuration target = Plugin.Configuration;
            try
            {
                foreach ((FieldInfo field, object? value) in resolved)
                {
                    OverrideKey key = new(field, target);
                    if (!Active.ContainsKey(key))
                        Active[key] = field.GetValue(target);
                    field.SetValue(target, value);
                }
            }
            catch (Exception ex)
            {
                error = ex.ToString();
            }

            activeCount = Active.Count;
        }

        if (error != null)
        {
            // 已經記進 Active 的那幾筆留著 —— 它們仍然需要被還原,直接 Pop 回去。
            Svc.Log.Error($"AutoDuty 設定覆寫:套用時擲例外,全部還原。{error}");
            Pop();
            return false;
        }

        Svc.Log.Information($"AutoDuty 設定覆寫:套用 {overrides.Count} 項,目前生效 {activeCount} 項。");
        return true;
    }

    /// <summary>把所有覆寫還原成使用者原本的值。沒有覆寫時回 <c>false</c>。</summary>
    internal static bool Pop()
    {
        int          count;
        List<string> failures = [];
        lock (Gate)
        {
            count = Active.Count;
            if (count == 0)
                return false;

            try
            {
                foreach ((OverrideKey key, object? previous) in Active)
                {
                    try
                    {
                        key.Field.SetValue(key.Target, previous);
                    }
                    catch (Exception ex)
                    {
                        // 單一欄位還原失敗不可以擋住其餘欄位,更不可以讓整張表清不掉。
                        failures.Add($"{key.Field.Name}: {ex.Message}");
                    }
                }
            }
            finally
            {
                // 🔴 清除放在 finally:例外不可以讓覆寫永久卡住。
                Active.Clear();
            }
        }

        if (failures.Count > 0)
            Svc.Log.Error($"AutoDuty 設定覆寫:還原 {count} 項時有 {failures.Count} 項失敗 —— {string.Join(" | ", failures)}");
        else
            Svc.Log.Information($"AutoDuty 設定覆寫:已還原 {count} 項。");

        return true;
    }

    /// <summary>
    /// 在<b>存檔序列化的那一刻</b>把被覆寫的欄位換回使用者的值,跑完 <paramref name="body"/> 再換回來。
    /// </summary>
    /// <remarks>
    /// 🔴 光是「IPC 不呼叫 Save()」不夠 —— 存檔是整份序列化,使用者之後動任何別的設定
    /// 都會把覆寫值一起寫下去。必須在存檔那一刻換回來。
    /// ⚠️ <paramref name="body"/> 只能做序列化(純 CPU)。<b>不要在裡面寫檔或碰 ImGui</b> ——
    /// 這裡是持著鎖跑的。ECommons 的 <c>SaveConfiguration</c> 是先拿到序列化結果、之後才寫檔,
    /// 所以掛在序列化那一支上剛好落在鎖外寫檔。
    /// 🔴 掛的是 <c>Serialize(object)</c> 不是 <c>SerializeAsBin</c> —— <c>IsBinary</c> 是 false,
    /// 存檔路徑不會走 <c>SerializeAsBin</c>,掛在那支上是死碼。
    /// </remarks>
    internal static T WithUserValues<T>(Func<T> body)
    {
        if (_inSwap)
            return body();

        // 🔑 日誌收集在鎖外寫:Serilog 的寫入端是檔案,鎖內不做檔案 I/O。
        List<string> restoreFailures = [];
        try
        {
            lock (Gate)
            {
                if (Active.Count == 0)
                    return body();

                // 先把「目前的(覆寫)值」抄下來,換成使用者的值,序列化,再換回去。
                List<(OverrideKey Key, object? Overridden)> swapped = new(Active.Count);
                _inSwap = true;
                try
                {
                    foreach ((OverrideKey key, object? user) in Active)
                    {
                        swapped.Add((key, key.Field.GetValue(key.Target)));
                        key.Field.SetValue(key.Target, user);
                    }

                    return body();
                }
                finally
                {
                    // 🔴 換回去放在 finally:序列化擲例外時不可以把使用者的值留在執行期設定上,
                    //    那會讓覆寫在無聲無息中失效。
                    foreach ((OverrideKey key, object? overridden) in swapped)
                    {
                        try
                        {
                            key.Field.SetValue(key.Target, overridden);
                        }
                        catch (Exception ex)
                        {
                            restoreFailures.Add($"{key.Field.Name}: {ex.Message}");
                        }
                    }

                    _inSwap = false;
                }
            }
        }
        finally
        {
            if (restoreFailures.Count > 0)
                Svc.Log.Error($"AutoDuty 設定覆寫:存檔後換回覆寫值失敗 {restoreFailures.Count} 項 —— {string.Join(" | ", restoreFailures)}");
        }
    }
}
