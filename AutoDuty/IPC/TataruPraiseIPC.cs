using Dalamud.Plugin.Ipc.Exceptions;
using ECommons.DalamudServices;
using System;

namespace AutoDuty.IPC;

/// <summary>
/// 單向橋接到「塔塔露誇獎」(TataruPraise)：AutoDuty <b>自己</b>停下來時請它念一句。
/// </summary>
/// <remarks>
/// 🔴 <b>零組件相依。</b>只用 Dalamud 原生 CallGate 的字串契約，不引 TataruPraise 的 dll ——
/// 兩邊裝／移除任一方永遠不會弄壞另一邊。對方沒安裝時本檔的每一條路徑都是安靜的 no-op。
/// <para>
/// 🔴 契約名與情境鍵逐字取自 TataruPraise 的 <c>IpcContract.cs</c> 與
/// <c>Core/PraiseCategory.cs</c>（<c>PraiseCategory.DutyRunStopped</c>）。CallGate 是純字串比對，
/// 名字打錯不會有任何錯誤訊息，只會永遠得到「這個頻道沒有人註冊」——<b>靜默斷線</b>。
/// 所以字串都寫成常數，不散在呼叫點上，也不要「順手整理」。
/// </para>
/// <para>
/// 🔴 <b>只能從主執行緒呼叫。</b>IPC 的實作是在<b>呼叫端</b>的執行緒上跑的，從背景 Task 叫過去
/// 等於把對方的程式碼拉到背景執行緒。唯一的呼叫點
/// （<c>AutoDuty.NotifyIfAutomationStoppedItself</c>）已經用
/// <c>Svc.Framework.RunOnFrameworkThread</c> 包起來。
/// </para>
/// <para>
/// ⚠️ 這是<b>單向通知</b>：回傳值只拿來寫記錄，不影響 AutoDuty 的任何流程，不重試，
/// 也絕不會因此觸發任何遊戲動作。純粹「出個聲」。
/// </para>
/// </remarks>
internal static class TataruPraiseIPC
{
    /// <summary><c>Func&lt;string, bool&gt;</c>：<b>這一個情境</b>現在出不出得了聲（總開關＋這個情境的開關＋這個情境有已合成的語音）。</summary>
    /// <remarks>📌 刻意<b>不</b>看冷卻：冷卻是「這一次剛好不出聲」，不是「不能出聲」。</remarks>
    internal const string TagIsAvailableFor = "TataruPraise.IsAvailableFor";

    /// <summary><c>Func&lt;string, bool&gt;</c>：從指定情境的誇獎池挑一句來念。</summary>
    internal const string TagPraise = "TataruPraise.Praise";

    /// <summary>
    /// 送過去的情境字串，逐字對應 TataruPraise 的 <c>PraiseCategory.DutyRunStopped</c>。
    /// </summary>
    /// <remarks>
    /// ⚠️ TataruPraise 拿這個字串當 <c>pool.json</c> 的鍵，<b>對不上就靜默不出聲</b>
    /// （它只會寫一行 Information 說這個情境沒有已合成語音的句子）。
    /// 📌 語意是「跑本<b>停下來</b>」而不是「跑完了」：正常跑完與中途出錯停住都走這個鍵。
    /// </remarks>
    internal const string CategoryDutyRunStopped = "跑本停止";

    /// <summary>
    /// 請塔塔露念一句。對方沒裝、關著、冷卻中、或池裡沒東西，這裡都是安靜的 no-op。
    /// </summary>
    /// <param name="reason">寫進記錄用的來源描述（英文原文），讓 log 分得出是哪一條邊觸發的。</param>
    /// <remarks>
    /// 🔴 <b>這個方法自己沒有去重。</b>呼叫端必須確定自己站在「狀態邊緣」上——
    /// AutoDuty 的 <c>Stage</c> setter <b>沒有</b> early-return 守衛，已經是 <c>Stopped</c> 時
    /// 再賦值一次仍會整段跑一遍，所以呼叫端那邊有一個 <c>_stage == Stage.Stopped</c> 的邊緣判斷。
    /// 少了它的失敗形式是「一直念」，不是報錯。
    /// </remarks>
    internal static void TryPraise(string reason)
    {
        try
        {
            // 先問 IsAvailableFor(情境)：問的是「這一個情境」出不出得了聲——總開關關著、
            // 使用者把這個情境關掉、或這個情境一句已合成的都沒有，都在這裡擋掉。
            // 🔴 不要退回去問 IsAvailable：那個問的是「整池」，於是「別的情境有句子、
            //    我這個情境一句都沒有」時它照樣回 true，這道閘門等於白做。
            // 這一步同時兼作「對方在不在」的探測——沒註冊就會在這裡擲 IpcNotReadyError。
            if (!Svc.PluginInterface.GetIpcSubscriber<string, bool>(TagIsAvailableFor)
                    .InvokeFunc(CategoryDutyRunStopped))
                return;

            bool accepted = Svc.PluginInterface.GetIpcSubscriber<string, bool>(TagPraise)
                               .InvokeFunc(CategoryDutyRunStopped);

            // Information 級：這是「使用者說沒出聲」時唯一問得出真相的一行。
            // ⚠️ 回傳 false 不是錯誤：可能還在冷卻，也可能池裡這個情境一句都沒有。
            Svc.Log.Information(
                $"[TataruPraise] {reason}：Praise(「{CategoryDutyRunStopped}」) 回傳 {accepted}。");
        }
        catch (IpcNotReadyError)
        {
            // 對方沒安裝／還沒載入。這是完全正常的狀態，刻意不寫 log——沒裝的人每次停下來都會走到這裡。
        }
        catch (Exception e)
        {
            // 對方版本不合、簽名對不上、或它自己的回呼爆掉。記一筆就好，
            // 絕不要讓它往上冒打斷停止流程的收尾。
            Svc.Log.Information($"[TataruPraise] 呼叫失敗（{reason}）：{e.Message}");
        }
    }
}
