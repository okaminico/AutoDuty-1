namespace AutoDuty.Multibox;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.PartyFunctions;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Helpers;
using Lumina.Excel.Sheets;

/// <summary>
/// 多開協調(Multibox)。移植自上游 erdelf/AutoDuty 的 AutoDuty/Multibox/MultiboxUtility.cs。
///
/// 🔴 紅線:一律手動觸發、預設關、沒有任何自動啟動點。
///    <see cref="MultiboxConfiguration.MultiBox"/> 標了 [JsonIgnore] ⇒ **永遠不會寫進設定檔**,
///    所以每次載入外掛都是關的,不存在「上次開著這次自動接上」的路徑。唯一的開關是
///    ConfigTab 裡使用者自己按的核取方塊。
///
/// 📌 與上游的差異(本 fork 結構較舊,不是照抄):
///  1. 上游 Plugin 的成員是 indexer/action 小寫,本 fork 是 Indexer/Action,且 InDungeon
///     是實例成員 ⇒ 一律走 Plugin 這個靜態單例。
///  2. 上游用 Newtonsoft(ConfigurationMain.JsonSerializerSettings)序列化路徑,本 fork 的
///     PathAction 掛的是 System.Text.Json 的 [JsonPropertyName] ⇒ 改用 BuildTab.jsonSerializerOptions,
///     否則欄名對不上會靜默序列化成空物件。
///  3. 上游 Stage 列舉有 Idle = 11,本 fork 沒有。上游的 `Stage = Idle; Stage = Reading_Path;`
///     在本 fork 的 Stage setter 裡兩者都不命中任何 case ⇒ 等價於直接設 Reading_Path。
///     🔴 **不可以拿 Stage.Stopped 代替 Idle** —— 本 fork 的 setter 對 Stopped 會呼叫
///     StopAndResetALL(),那會直接把整趟跑停掉。
///  4. 本 pin 的 CStringPointer **沒有** string → CStringPointer 的隱式轉換(只有反向),
///     上游的 InviteToParty(cid, client.CName, worldId) 在這裡編不過 ⇒ 自行 marshal 成 byte*。
///  5. InfoProxyPartyInvite.Instance() 一律判空後才解參考(上游直接解)。
///  6. World 查表改用 TryGetRow:WorldId 來自連線對端,是不可信輸入,GetRow 查無此列會擲
///     ArgumentOutOfRangeException。
/// </summary>
public static class MultiboxUtility
{
    public class MultiboxConfiguration
    {
        // 🔴 [JsonIgnore]:多開開關是「執行期狀態」不是「設定」,絕不落地。
        // 這正是「預設關且無自動啟動點」的保證 —— 外掛每次載入都是 false。
        private bool multiBox = false;

        [Newtonsoft.Json.JsonIgnore]
        public bool MultiBox
        {
            get => this.multiBox;
            set
            {
                if (this.multiBox == value)
                    return;
                this.multiBox = value;

                Set(this.multiBox);
            }
        }

        public bool          SynchronizePath { get; set; } = true;
        public bool          Host            { get; set; } = false;
        public string        PipeName        { get; set; } = "AutoDutyPipe";
        public string        ServerName      { get; set; } = ".";
        public TransportType TransportType   { get; set; } = TransportType.NamedPipe;
        public string        ServerAddress   { get; set; } = "127.0.0.1";
        public int           ServerPort      { get; set; } = 1716;
    }

    public static MultiboxConfiguration Config => ConfigurationMain.Instance.multibox;

    private const string SERVER_AUTH_KEY = "AD_Server_Auth!";
    private const string CLIENT_AUTH_KEY = "AD_Client_Auth!";
    private const string CLIENT_CID_KEY  = "CLIENT_CID";
    private const string PARTY_INVITE    = "PARTY_INVITE";

    private const string KEEPALIVE_KEY          = "KEEP_ALIVE";
    private const string KEEPALIVE_RESPONSE_KEY = "KEEP_ALIVE received";

    private const string DUTY_QUEUE_KEY = "DUTY_QUEUE";
    private const string DUTY_EXIT_KEY  = "DUTY_EXIT";

    private const string DEATH_KEY       = "DEATH";
    private const string UNDEATH_KEY     = "UNDEATH";
    private const string DEATH_RESET_KEY = "DEATH_RESET";

    private const string PATH_STEPS = "PATH_STEPS";

    private const string STEP_COMPLETED = "STEP_COMPLETED";
    private const string STEP_START     = "STEP_START";

    internal static bool stepBlock = false;

    public static bool MultiboxBlockingNextStep
    {
        get
        {
            if (!Config.MultiBox)
                return false;

            return stepBlock;
        }
        set
        {
            DebugLog($"blocking step: {stepBlock} to {value}");
            if (!Config.MultiBox)
                return;

            if (!value)
                if (Config.Host)
                    Server.SendStepStart();

            if (stepBlock == value)
                return;

            stepBlock = value;

            if (stepBlock)
                if (Config.Host)
                {
                    Plugin.Action = "Waiting for clients";
                    Server.CheckStepProgress();
                }
                else
                {
                    Client.SendStepCompleted();
                }
        }
    }

    public static void IsDead(bool dead)
    {
        // 🔴 與上游不同:上游這裡寫的是 `if (Config.MultiBox) return;`,那會讓死亡同步在
        // 多開「開啟」時整個失效(明顯是筆誤,判斷方向反了)。這裡改成未開啟才 return。
        if (!Config.MultiBox)
            return;

        if (!Config.Host)
            Client.SendDeath(dead);
        else
            Server.CheckDeaths();
    }

    public static void Set(bool on)
    {
        if (on)
            ConfigurationMain.Instance.GetCurrentConfig.DutyModeEnum = DutyMode.Regular;

        if (Config.Host)
            Server.Set(on);
        else
            Client.Set(on);
    }

    internal static class Server
    {
        public const             int             MAX_SERVERS   = 3;
        private static readonly  StreamString?[] streams       = new StreamString?[MAX_SERVERS];
        internal static readonly ClientInfo?[]   clients       = new ClientInfo?[MAX_SERVERS];
        private static readonly  Queue<string>[] messageQueues = [new(), new(), new()];

        internal static readonly DateTime[] keepAlives    = new DateTime[MAX_SERVERS];
        internal static readonly bool[]     stepConfirms  = new bool[MAX_SERVERS];
        private static readonly  bool[]     deathConfirms = new bool[MAX_SERVERS];

        private static ITransport?              transport;
        private static CancellationTokenSource? serverCts;

        /// <summary>伺服器是否真的起來了(供 UI 顯示,不參與任何決策)。</summary>
        internal static bool Running => transport != null;

        /// <summary>目前已認證連上的用戶端數量(供 UI 顯示)。</summary>
        internal static int ConnectedCount => clients.Count(c => c != null);

        public static void Set(bool on)
        {
            try
            {
                if (on)
                    StartServer();
                else
                    StopServer();
            }
            catch (Exception ex)
            {
                ErrorLog(ex.ToString());
            }
        }

        private static void StartServer()
        {
            try
            {
                if (transport != null) return;

                transport = Config.TransportType switch
                {
                    TransportType.NamedPipe => new NamedPipeTransport(Config.PipeName),
                    TransportType.Tcp       => new TcpTransport(Config.ServerPort),
                    _                       => throw new NotImplementedException(Config.TransportType.ToString()),
                };

                transport.StartServer(MAX_SERVERS);
                serverCts = new CancellationTokenSource();
                Task.Run(() => AcceptLoop(serverCts.Token), serverCts.Token);
                Svc.Log.Information($"[Multibox] 主機端已啟動,傳輸方式 {Config.TransportType}");
            }
            catch (Exception ex)
            {
                ErrorLog($"StartServer error: {ex}");
            }
        }

        private static void StopServer()
        {
            try
            {
                serverCts?.Cancel();
                transport?.StopServer();
                transport?.Dispose();
                transport = null;
                serverCts = null;

                for (int i = 0; i < MAX_SERVERS; i++)
                {
                    streams[i] = null;
                    clients[i] = null;
                    messageQueues[i].Clear();
                    keepAlives[i]   = DateTime.MinValue;
                    stepConfirms[i] = false;
                }

                if (Plugin is { InDungeon: false })
                {
                    Chat.ExecuteCommand("/partycmd breakup");

                    SchedulerHelper.ScheduleAction("MultiboxServer PartyBreakup Accept", () =>
                                                                                         {
                                                                                             unsafe
                                                                                             {
                                                                                                 InfoProxyPartyInvite* invite = InfoProxyPartyInvite.Instance();
                                                                                                 if (invite == null)
                                                                                                 {
                                                                                                     SchedulerHelper.DescheduleAction("MultiboxServer PartyBreakup Accept");
                                                                                                     return;
                                                                                                 }

                                                                                                 Utf8String inviterName = invite->InviterName;

                                                                                                 if (UniversalParty.Length <= 1)
                                                                                                 {
                                                                                                     SchedulerHelper.DescheduleAction("MultiboxServer PartyBreakup Accept");
                                                                                                     return;
                                                                                                 }

                                                                                                 if (GenericHelpers.TryGetAddonByName("SelectYesno", out AtkUnitBase* addonSelectYesno) &&
                                                                                                     GenericHelpers.IsAddonReady(addonSelectYesno))
                                                                                                 {
                                                                                                     // 🔴 這是 500 毫秒重複排程,窗還在就會一直按。確認框「關閉中」的那幾幀
                                                                                                     //    TryGetAddonByName 與 IsAddonReady 三關全過,再按一次就是攔不到的存取違規
                                                                                                     //    (AddonMaster.Yes() 還會強制翻 NodeFlags 繞過遊戲自己的防重按)。
                                                                                                     //    順序:先看守衛擋不擋(擋著的那幾幀連文字都不讀)→ 讀提示文字 →
                                                                                                     //    讀到 U+FFFD 這一幀不碰 → 登記 → 按。
                                                                                                     if (!AddonPressGuard.IsHeld("SelectYesno", addonSelectYesno))
                                                                                                     {
                                                                                                         AddonMaster.SelectYesno yesno  = new(addonSelectYesno);
                                                                                                         string                 prompt = yesno.Text;
                                                                                                         if (!AddonPressGuard.IsTextCorrupt("SelectYesno", prompt)
                                                                                                             && AddonPressGuard.TryBeginPress("SelectYesno", addonSelectYesno))
                                                                                                         {
                                                                                                             if (prompt.Contains(inviterName.ToString()))
                                                                                                                 yesno.Yes();
                                                                                                             else
                                                                                                                 yesno.No();
                                                                                                         }
                                                                                                     }
                                                                                                 }

                                                                                                 if (GenericHelpers.TryGetAddonByName("Social", out AtkUnitBase* addonSocial) &&
                                                                                                     GenericHelpers.IsAddonReady(addonSocial))
                                                                                                 {
                                                                                                     ErrorLog("/partycmd breakup opened the party menu instead");
                                                                                                     SchedulerHelper.DescheduleAction("MultiboxServer PartyBreakup Accept");
                                                                                                     return;
                                                                                                 }
                                                                                             }
                                                                                         }, 500, false);
                }

                Svc.Log.Information("[Multibox] 主機端已停止");
            }
            catch (Exception ex)
            {
                ErrorLog($"StopServer error: {ex}");
            }
        }

        private static async void AcceptLoop(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    Stream s   = await transport!.AcceptConnectionAsync(ct);
                    int    idx = -1;
                    for (int i = 0; i < MAX_SERVERS; i++)
                    {
                        if (streams[i] == null)
                        {
                            idx = i;
                            break;
                        }
                    }

                    if (idx == -1)
                    {
                        try
                        {
                            await s.DisposeAsync();
                        }
                        catch (Exception ex)
                        {
                            ErrorLog(ex.ToString());
                        }

                        continue;
                    }

                    streams[idx] = new StreamString(s);

                    int capturedIdx = idx;
                    _ = Task.Run(() => ConnectionHandler(s, capturedIdx, ct), ct);
                }
            }
            catch (OperationCanceledException)
            {
                DebugLog("AcceptLoop ended due to cancellation");
            }
            catch (Exception ex)
            {
                ErrorLog($"AcceptLoop error: {ex}");
            }
        }

        private static async void ConnectionHandler(Stream stream, int index, CancellationToken ct)
        {
            try
            {
                await using Stream s = stream;
                if (streams[index] == null)
                    return;
                StreamString ss = streams[index]!;
                ss.WriteString(SERVER_AUTH_KEY);
                if (ss.ReadString() != CLIENT_AUTH_KEY)
                    return;

                Svc.Log.Information($"[Multibox] 用戶端 {index} 已通過認證");
                keepAlives[index] = DateTime.Now;
                Task sendTask = Task.Run(async () => await ServerSendThread(index, ct), ct);

                while (!ct.IsCancellationRequested && !sendTask.IsCompleted)
                {
                    await Task.Delay(100, ct);
                    string   message = ss.ReadString().Trim();
                    string[] split   = message.Split("|");

                    switch (split[0])
                    {
                        case "" when message.Length == 0:
                            DebugLog($"Client {index} closed the connection.");
                            return;
                        case CLIENT_CID_KEY:
                            // 對端送來的欄位是不可信輸入,格式不對就丟掉不要讓整條連線炸掉。
                            if (split.Length < 4                          ||
                                !ulong.TryParse(split[1], out ulong cid)  ||
                                !ushort.TryParse(split[3], out ushort wid))
                            {
                                ErrorLog($"Malformed {CLIENT_CID_KEY} from {index}: {message}");
                                break;
                            }

                            clients[index] = new ClientInfo(cid, split[2], wid);

                            // 🔴🔴 卸載期旁路:同上 —— 無延遲的 RunOnTick 在
                            //    IsFrameworkUnloading 為真時會就地在呼叫端執行緒執行。
                            //    ConnectionHandler 是 async void,從 Task.Run 進來且每圈
                            //    await Task.Delay(100) ⇒ 這裡一定是執行緒池的執行緒。
                            //    委派裡有 PartyHelper.IsPartyMember、Player.CurrentWorldId、
                            //    InfoProxyPartyInvite.Instance()->InviteToParty(...) —— 全是
                            //    原生記憶體存取,卸載期正是那些結構被拆掉的時候。
                            //    🔑 跳過等同於「這次沒送出邀請」,對端本來就要能處理
                            //    「邀請沒來」(它只是等不到 PARTY_INVITE)。
                            if (SkipDuringFrameworkUnload("送出組隊邀請"))
                                break;

                            _ = Svc.Framework.RunOnTick(() =>
                                                        {
                                                            unsafe
                                                            {
                                                                ClientInfo? client = clients[index];
                                                                if (client == null)
                                                                    return;

                                                                Svc.Log.Information($"[Multibox] 收到用戶端識別:{client.CID} {client.CName} {client.WorldId}");

                                                                if (!PartyHelper.IsPartyMember(client.CID))
                                                                {
                                                                    InfoProxyPartyInvite* invite = InfoProxyPartyInvite.Instance();
                                                                    if (invite == null)
                                                                    {
                                                                        ErrorLog("InfoProxyPartyInvite unavailable, skipping invite");
                                                                        return;
                                                                    }

                                                                    // 📌 本 pin 的 ECommons 是 Player.CurrentWorldId(uint),
                                                                    // 不是上游較新版的 Player.CurrentWorld.RowId。
                                                                    if (client.WorldId == Player.CurrentWorldId)
                                                                    {
                                                                        // 📌 本 pin 的 CStringPointer 沒有 string 的隱式轉換,
                                                                        // 必須自己 marshal 成 NUL 結尾的 byte*。
                                                                        byte[] nameBytes = Encoding.UTF8.GetBytes(client.CName + "\0");
                                                                        fixed (byte* pName = nameBytes)
                                                                            invite->InviteToParty(client.CID, pName, client.WorldId);
                                                                    }
                                                                    else
                                                                    {
                                                                        invite->InviteToPartyContentId(client.CID, 0);
                                                                    }

                                                                    ss.WriteString(PARTY_INVITE);
                                                                }

                                                                stepConfirms[index] = false;
                                                            }
                                                        }, cancellationToken: ct);
                            break;
                        case KEEPALIVE_KEY:
                            ss.WriteString(KEEPALIVE_RESPONSE_KEY);
                            break;
                        case KEEPALIVE_RESPONSE_KEY:
                            break;
                        case STEP_COMPLETED:
                            stepConfirms[index] = true;
                            CheckStepProgress();
                            break;
                        case DEATH_KEY:
                            deathConfirms[index] = true;
                            CheckDeaths();
                            break;
                        case UNDEATH_KEY:
                            deathConfirms[index] = false;
                            break;
                        default:
                            ss.WriteString($"Unknown Message from {index}: {message}");
                            continue;
                    }

                    keepAlives[index] = DateTime.Now;
                }
            }
            catch (OperationCanceledException)
            {
                DebugLog($"Connection handler ended due to cancellation {index}");
            }
            catch (Exception e)
            {
                ErrorLog($"ConnectionHandler error {index}: {e.Message}\n{e.StackTrace}");
            }
            finally
            {
                streams[index] = null;
                clients[index] = null;
            }
        }

        private static async Task ServerSendThread(int index, CancellationToken ct)
        {
            try
            {
                DebugLog("SEND Initialized with " + index);

                while (!ct.IsCancellationRequested && streams[index] != null)
                {
                    if (messageQueues[index].Count > 0)
                    {
                        string message = messageQueues[index].Dequeue();
                        streams[index]?.WriteString(message);
                    }
                    else if ((DateTime.Now - keepAlives[index]).TotalSeconds > 15)
                    {
                        // if no messages to send and the connection is stale, send a keepalive to check.
                        // (Usually this is the clients job but the tcp socket doesn't die immediately)
                        streams[index]?.WriteString(KEEPALIVE_KEY);
                        await Task.Delay(1000, ct);
                    }

                    await Task.Delay(100, ct);
                }
            }
            catch (OperationCanceledException)
            {
                DebugLog($"SendLoop ended due to cancellation for {index}");
            }
            catch (Exception e)
            {
                ErrorLog($"SERVER SEND ERROR for {index}: " + e);
            }
        }

        public static bool AllInParty()
        {
            for (int i = 0; i < MAX_SERVERS; i++)
            {
                if (clients[i] == null || !PartyHelper.IsPartyMember(clients[i]!.CID))
                    return false;
            }

            return true;
        }

        public static void CheckDeaths()
        {
            if (deathConfirms.All(x => x) && Player.IsDead)
            {
                for (int i = 0; i < deathConfirms.Length; i++)
                    deathConfirms[i] = false;

                DebugLog("All dead");
                SendToAllClients(DEATH_RESET_KEY);
            }
            else
            {
                DebugLog("Not all clients are dead yet, waiting for more death.");
            }
        }

        public static void CheckStepProgress()
        {
            if ((Plugin.Stage != Stage.Looping && Plugin.Indexer >= 0 && Plugin.Indexer < Plugin.Actions.Count && Plugin.Actions[Plugin.Indexer].Tag == ActionTag.Treasure || stepConfirms.All(x => x)) && stepBlock)
            {
                for (int i = 0; i < stepConfirms.Length; i++)
                    stepConfirms[i] = false;

                DebugLog("All clients completed the step");
                stepBlock = false;
            }
            else
            {
                DebugLog("Not all clients have completed the step yet, waiting for more confirmations.");
            }
        }

        public static void SendStepStart()
        {
            DebugLog("Synchronizing Clients to Server step");
            SendToAllClients($"{STEP_START}|{Plugin.Indexer}");
        }

        public static void ExitDuty()
        {
            DebugLog("exiting duty");
            SendToAllClients(DUTY_EXIT_KEY);
            for (int i = 0; i < stepConfirms.Length; i++)
                stepConfirms[i] = false;
        }

        public static void Queue()
        {
            DebugLog("Queue initiated");
            SendToAllClients(DUTY_QUEUE_KEY);
            for (int i = 0; i < stepConfirms.Length; i++)
                stepConfirms[i] = false;
            stepBlock = false;
        }

        // 📌 本 fork 的 PathAction 掛的是 System.Text.Json 的 [JsonPropertyName],
        // 用 Newtonsoft 序列化欄名會對不上 ⇒ 一律走 BuildTab.jsonSerializerOptions。
        public static void SendPath() =>
            SendToAllClients($"{PATH_STEPS}|{JsonSerializer.Serialize(Plugin.Actions, BuildTab.jsonSerializerOptions)}");

        private static void SendToAllClients(string message)
        {
            DebugLog("Enqueuing to send: " + message);
            foreach (Queue<string> queue in messageQueues)
                queue.Enqueue(message);
        }

        internal record ClientInfo(ulong CID, string CName, ushort WorldId)
        {
            private string? world;

            // 🔴 WorldId 來自連線對端(不可信),GetRow 查無此列會擲 ArgumentOutOfRangeException
            // ⇒ 一律 TryGetRow。
            public string World =>
                this.world ??= Svc.Data.Excel.GetSheet<World>().TryGetRow(this.WorldId, out World row)
                                   ? row.Name.GetText()
                                   : $"#{this.WorldId}";
        }
    }

    internal static class Client
    {
        private static StreamString?            clientSS;
        private static CancellationTokenSource? clientCts;

        /// <summary>用戶端是否已建立串流(供 UI 顯示,不參與任何決策)。</summary>
        internal static bool Connected => clientSS != null;

        /// <summary>已按下開關但還沒連上(供 UI 區分「連線中」與「未啟用」)。</summary>
        internal static bool Connecting => clientCts != null && clientSS == null;

        public static void Set(bool on)
        {
            if (on)
            {
                clientCts = new CancellationTokenSource();
                Task.Run(() => ClientConnectionThread(clientCts.Token), clientCts.Token);
            }
            else
            {
                try
                {
                    clientCts?.Cancel();
                }
                catch (Exception ex)
                {
                    ErrorLog(ex.ToString());
                }

                clientSS  = null;
                clientCts = null;
            }
        }

        private static async void ClientConnectionThread(CancellationToken ct)
        {
            try
            {
                using ITransport transport = Config.TransportType switch
                {
                    TransportType.NamedPipe => new NamedPipeTransport(Config.PipeName, Config.ServerName),
                    TransportType.Tcp       => new TcpTransport(Config.ServerAddress, Config.ServerPort),
                    _                       => throw new NotImplementedException(Config.TransportType.ToString()),
                };

                Svc.Log.Information($"[Multibox] 連線至主機端({Config.TransportType})...");
                await using Stream clientStream = await transport.ConnectToServerAsync(ct);

                clientSS = new StreamString(clientStream);

                if (clientSS.ReadString() == SERVER_AUTH_KEY)
                {
                    clientSS.WriteString(CLIENT_AUTH_KEY);

                    // 🔴🔴 卸載期旁路:無延遲的 RunOnTick 在 Framework.IsFrameworkUnloading
                    //    為真時不是排隊,而是直接轉呼叫 RunOnFrameworkThread,而後者在
                    //    IsInFrameworkUpdateThread || IsFrameworkUnloading 時「就地在呼叫端
                    //    執行緒執行」(本 pin Dalamud/Game/Framework.cs)。這一整支是
                    //    async void,await ConnectToServerAsync 之後跑在執行緒池的執行緒上
                    //    ⇒ 關遊戲那一瞬間委派會在那條執行緒上讀 Player.CID／Player.Name／
                    //    Player.CurrentWorldId,也就是對 IObjectTable 每格重用、Address 就地
                    //    改寫的共用包裝解參考。失敗形式是 AccessViolationException,
                    //    而那在 .NET Core 是 corrupted-state exception,try/catch 攔不到。
                    //    🔑 卸載期直接跳過:沒送出識別等同於「這次沒接上多開」,
                    //    而多開本來就會在外掛卸載時整組收掉。
                    if (!SkipDuringFrameworkUnload("送出本機角色識別"))
                        _ = Svc.Framework.RunOnTick(() =>
                                                {
                                                    if (Player.CID != 0)
                                                        clientSS.WriteString($"{CLIENT_CID_KEY}|{Player.CID}|{Player.Name}|{Player.CurrentWorldId}");
                                                }, cancellationToken: ct);

                    _ = Task.Run(() => ClientKeepAliveThread(ct), ct);
                    while (!ct.IsCancellationRequested)
                    {
                        string   message = clientSS.ReadString().Trim();
                        string[] split   = message.Split("|");

                        switch (split[0])
                        {
                            case "" when message.Length == 0:
                                DebugLog("Server closed the connection.");
                                return;
                            case STEP_START:
                                if (split.Length > 1 && int.TryParse(split[1], out int step))
                                {
                                    Plugin.Indexer = step;
                                    stepBlock      = false;
                                    // 📌 上游這裡是 `Stage = Idle; Stage = Reading_Path;`。本 fork 沒有
                                    // Stage.Idle,而兩者在本 fork 的 setter 都不命中任何 case ⇒ 直接設
                                    // Reading_Path 等價。🔴 不可改用 Stage.Stopped(會 StopAndResetALL)。
                                    Plugin.Stage = Stage.Reading_Path;
                                }

                                break;
                            case KEEPALIVE_KEY:
                                clientSS.WriteString(KEEPALIVE_RESPONSE_KEY);
                                break;
                            case KEEPALIVE_RESPONSE_KEY:
                                break;
                            case DUTY_QUEUE_KEY:
                                QueueHelper.InvokeAcceptOnly();
                                break;
                            case DUTY_EXIT_KEY:
                                stepBlock = false;
                                ExitDutyHelper.Invoke();
                                break;
                            case PARTY_INVITE:
                                SchedulerHelper.ScheduleAction("MultiboxClient PartyInvite Accept", () =>
                                                                                                    {
                                                                                                        unsafe
                                                                                                        {
                                                                                                            if (UniversalParty.Length > 1)
                                                                                                            {
                                                                                                                PartyHelper.LeaveParty();
                                                                                                                return;
                                                                                                            }

                                                                                                            InfoProxyPartyInvite* invite = InfoProxyPartyInvite.Instance();
                                                                                                            if (invite == null)
                                                                                                                return;

                                                                                                            Utf8String inviterName = invite->InviterName;
                                                                                                            if (invite->InviterWorldId != 0                                                    &&
                                                                                                                UniversalParty.Length <= 1                                                     &&
                                                                                                                GenericHelpers.TryGetAddonByName("SelectYesno", out AtkUnitBase* addonSelectYesno) &&
                                                                                                                GenericHelpers.IsAddonReady(addonSelectYesno))
                                                                                                            {
                                                                                                                // 同上:500 毫秒重複排程 + AddonMaster 會繞過遊戲的防重按,
                                                                                                                // 「關閉中」那幾幀的重按只有 AddonPressGuard 擋得住。
                                                                                                                // 順序:守衛擋著就連文字都不讀 → 讀提示 → U+FFFD 這一幀不碰 → 登記 → 按。
                                                                                                                if (AddonPressGuard.IsHeld("SelectYesno", addonSelectYesno))
                                                                                                                    return;

                                                                                                                AddonMaster.SelectYesno yesno  = new(addonSelectYesno);
                                                                                                                string                 prompt = yesno.Text;
                                                                                                                if (AddonPressGuard.IsTextCorrupt("SelectYesno", prompt)
                                                                                                                    || !AddonPressGuard.TryBeginPress("SelectYesno", addonSelectYesno))
                                                                                                                    return;

                                                                                                                if (prompt.Contains(inviterName.ToString()))
                                                                                                                {
                                                                                                                    yesno.Yes();
                                                                                                                    SchedulerHelper.DescheduleAction("MultiboxClient PartyInvite Accept");
                                                                                                                }
                                                                                                                else
                                                                                                                {
                                                                                                                    yesno.No();
                                                                                                                }
                                                                                                            }
                                                                                                        }
                                                                                                    }, 500, false);
                                break;
                            case PATH_STEPS:
                                List<PathAction>? steps = JsonSerializer.Deserialize<List<PathAction>>(message[(split[0].Length + 1)..], BuildTab.jsonSerializerOptions);
                                if (steps is { Count: > 0 })
                                {
                                    DebugLog("setting steps from host");
                                    Plugin.Actions = steps;
                                }

                                break;
                            default:
                                ErrorLog("Unknown response: " + message);
                                break;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                DebugLog("ClientConnection ended due to cancellation");
            }
            catch (Exception e)
            {
                ErrorLog($"Client ERROR: {e.Message}\n{e.StackTrace}");
            }
            finally
            {
                Config.MultiBox = false;
            }
        }

        private static async void ClientKeepAliveThread(CancellationToken ct)
        {
            try
            {
                await Task.Delay(1000, ct);
                while (!ct.IsCancellationRequested && clientSS != null)
                {
                    clientSS?.WriteString(KEEPALIVE_KEY);
                    await Task.Delay(10000, ct);
                }
            }
            catch (OperationCanceledException)
            {
                DebugLog("ClientKeepalive ended due to cancellation");
            }
            catch (Exception e)
            {
                ErrorLog("Client KEEPALIVE Error: " + e);
            }
        }

        public static void SendStepCompleted()
        {
            if (clientSS == null)
            {
                DebugLog("Client not connected, cannot send step completed.");
                return;
            }

            Plugin.Action = "Waiting for others";
            clientSS.WriteString(STEP_COMPLETED);
            DebugLog("Step completed sent to server.");
        }

        public static void SendDeath(bool dead)
        {
            if (clientSS == null)
            {
                DebugLog("Client not connected, cannot send death.");
                return;
            }

            clientSS.WriteString(dead ? DEATH_KEY : UNDEATH_KEY);
            DebugLog("Death sent to server.");
        }
    }

    /// <summary>同一則卸載期訊息的重印間隔(毫秒)。</summary>
    private const long UnloadLogIntervalMs = 10000;

    /// <summary>節流表上限,避免鍵意外發散時無限成長。</summary>
    private const int MaxTrackedUnloadKeys = 32;

    private static readonly Dictionary<string, long> UnloadLogTimes = [];

    /// <summary>
    /// 判斷「現在是不是 Dalamud 卸載期,而且我不在 framework 執行緒上」。
    /// 為真時呼叫端應該直接放棄這次要排給 framework 執行緒的工作。
    /// </summary>
    /// <remarks>
    /// 🔴 為什麼需要:<c>Svc.Framework.RunOnTick</c>(無 delay／delayTicks)與
    /// <c>RunOnFrameworkThread</c> 在 <c>IsFrameworkUnloading</c> 為真時<b>就地在呼叫端的
    /// 執行緒上執行</b>委派(本 pin <c>Dalamud/Game/Framework.cs</c>)——
    /// 也就是說「丟回 framework 執行緒再讀原生狀態」這層保護,在關遊戲／停用外掛那一
    /// 瞬間整個失效。AccessViolationException 在 .NET Core 是 corrupted-state exception,
    /// <c>try</c>/<c>catch</c> 與 <c>HookSafety.ExecuteSafe</c> 都攔不到,使用者看到的是遊戲直接關掉。
    /// <br/><br/>
    /// 📌 <b>已經在 framework 執行緒上時一律回 false</b>(那本來就是安全的執行緒),
    /// 所以非卸載期的行為與改動前逐字相同。
    /// <br/><br/>
    /// 🔴 節流刻意<b>不用</b> <c>EzThrottler</c>:那是整個外掛共用的靜態 <c>Dictionary</c> 且零同步,
    /// 而這條路徑跑在連線執行緒上,並行插入弄壞的是整張表 —— 連帶弄壞外掛裡所有模組的節流。
    /// 所以自帶字典＋自己的鎖,而且<b>鎖內只碰字典</b>:不寫 log、不做 I/O。
    /// <br/><br/>
    /// 📌 診斷寫 Information:使用者跑 LogLevel 1,這一級一定收得到,又不會被 Debug 的數十萬行淹沒。
    /// </remarks>
    private static bool SkipDuringFrameworkUnload(string what)
    {
        if (!Svc.Framework.IsFrameworkUnloading || Svc.Framework.IsInFrameworkUpdateThread)
            return false;

        if (ShouldLogUnload(what))
            Svc.Log.Information($"[Multibox] Dalamud 正在卸載,已跳過「{what}」。卸載期的 RunOnTick 會就地在呼叫端的執行緒上執行,保護不了原生記憶體存取;此時多開功能失效可以接受,遊戲崩潰不行。");

        return true;
    }

    /// <summary>首次必放行,之後每 <see cref="UnloadLogIntervalMs"/> 毫秒放行一次。</summary>
    private static bool ShouldLogUnload(string key)
    {
        long now = Environment.TickCount64;
        lock (UnloadLogTimes)
        {
            if (UnloadLogTimes.TryGetValue(key, out long last) && now - last < UnloadLogIntervalMs)
                return false;
            if (UnloadLogTimes.Count >= MaxTrackedUnloadKeys && !UnloadLogTimes.ContainsKey(key))
                UnloadLogTimes.Clear();
            UnloadLogTimes[key] = now;
            return true;
        }
    }

    private static void DebugLog(string message) =>
        Svc.Log.Debug($"Pipe Connection: {message}");

    private static void ErrorLog(string message) =>
        Svc.Log.Error($"Pipe Connection: {message}");

    private class StreamString(Stream ioStream)
    {
        private readonly UnicodeEncoding streamEncoding = new();

        public string ReadString()
        {
            int b1 = ioStream.ReadByte();
            int b2 = ioStream.ReadByte();

            if (b1 == -1 || b2 == -1)
            {
                DebugLog("End of stream reached.");
                return string.Empty;
            }

            int    len      = b1 * 256 + b2;
            byte[] inBuffer = new byte[len];
            int    n        = 0;
            while (n < len)
            {
                int c = ioStream.Read(inBuffer, n, len - n);
                if (c == 0)
                {
                    ErrorLog("Stream closed unexpectedly");
                    return string.Empty;
                }

                n += c;
            }

            string readString = this.streamEncoding.GetString(inBuffer);

            DebugLog("Reading: " + readString);
            return readString;
        }

        public int WriteString(string outString)
        {
            DebugLog("Writing: " + outString);

            byte[] outBuffer = this.streamEncoding.GetBytes(outString);
            int    len       = outBuffer.Length;
            if (len > ushort.MaxValue)
                throw new ArgumentException("String too long to write to stream");
            ioStream.WriteByte((byte)(len / 256));
            ioStream.WriteByte((byte)(len & 255));
            ioStream.Write(outBuffer, 0, len);
            ioStream.Flush();

            return outBuffer.Length + 2;
        }
    }
}
