using AutoDuty.Windows;
using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Networking.Http;

namespace AutoDuty.Updater
{
    internal static class GitHubHelper
    {
        const string CLIENT_ID = "Iv23liWV5R21nasKaQjP";

        /// <summary>
        /// 副本路徑檔與其 MD5 清單的來源。
        /// <para>
        /// 🔴 路徑檔**不隨外掛出貨** —— <c>Plugin.PathsDirectory</c> 是
        /// <c>&lt;pluginConfigs&gt;/AutoDuty/paths</c>,啟動時只會被建成空目錄,
        /// 內容唯一的來源就是「路徑」分頁那顆手動更新按鈕從這個位址下載。
        /// 也就是說 <b>這個常數決定了使用者實際跑的是誰的路徑資料</b>。
        /// </para>
        /// <para>
        /// 指向 okaminico/AutoDuty-1 的 tw-fix(myfork),而不是 ffxiv-tc-port/AutoDuty 的
        /// tc-7.20(origin)——tw-fix 定期合併 origin/tc-7.20,並額外帶有尚未回饋上游的
        /// 本地路徑修正(例如黃金谷 StopForCombat 拖怪修正)。
        /// </para>
        /// </summary>
        internal const string PathRepoBaseUrl = "https://raw.githubusercontent.com/okaminico/AutoDuty-1/refs/heads/tw-fix/";

        private static readonly SocketsHttpHandler _handler = new() { AutomaticDecompression = DecompressionMethods.All, ConnectCallback = new HappyEyeballsCallback().ConnectCallback };

        private static readonly HttpClient _client = new(_handler) { Timeout = TimeSpan.FromSeconds(20) };

        internal static async Task<bool> DownloadFileAsync(string url, string localPath)
        {
            try
            {
                HttpResponseMessage response = await _client.GetAsync(url);
                response.EnsureSuccessStatusCode();

                string content = await response.Content.ReadAsStringAsync();
                await File.WriteAllTextAsync(localPath, content);
                return true;
            }
            catch (Exception ex)
            {
                Svc.Log.Error($"{ex}");
                return false;
            }
        }

        internal static async Task<Dictionary<string, string>?> GetPathFileListAsync()
        {
            try
            {
                // Temporary handler and client, to avoid default headers below
                using SocketsHttpHandler handler = new();
                handler.AutomaticDecompression = DecompressionMethods.All;
                handler.ConnectCallback = new HappyEyeballsCallback().ConnectCallback;
                using HttpClient client = new(handler);
                client.Timeout = TimeSpan.FromSeconds(20);

                // 🔴 指向本 fork 而不是 ffxivcode:原上游已於 2026-01 封存(README 自掛
                // ARCHIVED 公告,開發移至 erdelf/AutoDuty),那份清單不會再更新;而且它列的
                // 是國際服的路徑檔,會把我方為台服客製過的路徑靜默覆蓋掉(實測 63 個檔)。
                var md5List = await client.GetFromJsonAsync<Dictionary<string, string>>(PathRepoBaseUrl + "AutoDuty/Resources/md5s.json");
                return md5List ?? [];
            }
            catch (Exception ex)
            {
                Svc.Log.Error($"{ex}");
                return null;
            }
        }

        internal static async Task<UserCode?> GetUserCode()
        {
            try
            {
                var uri = new Uri("https://github.com/login/device/code");
                var parameters = new FormUrlEncodedContent([new KeyValuePair<string, string>("client_id", CLIENT_ID)]);
                if (!_client.DefaultRequestHeaders.Accept.Any(h => h.MediaType == "application/json"))
                    _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                var response = await _client.PostAsync(uri, parameters);
                var jsonString = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<UserCode>(jsonString, BuildTab.jsonSerializerOptions);
            }
            catch (Exception ex)
            {
                Svc.Log.Error($"{ex}");
                return null;
            }
        }

        internal static async Task<PollResponseClass?> PollResponse(UserCode userCode)
        {
            try
            {
                var uri = new Uri("https://github.com/login/oauth/access_token");
                var parameters = new FormUrlEncodedContent(
                [
                    new KeyValuePair<string, string>("client_id", CLIENT_ID),
                    new KeyValuePair<string, string>("device_code", userCode.Device_Code),
                    new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code")
                ]);
                if (!_client.DefaultRequestHeaders.Accept.Any(h => h.MediaType == "application/json"))
                    _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                var response = await _client.PostAsync(uri, parameters);
                var jsonString = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<PollResponseClass>(jsonString, BuildTab.jsonSerializerOptions);
            }
            catch (Exception ex)
            {
                Svc.Log.Error($"{ex}");
                return null;
            }
        }

        internal static async Task<string?> FileIssue(string title, string whatHappened, string reproSteps, string accessToken)
        {
            try
            {
                var body = $"What Happened?\n\n{whatHappened}\n\nVersion Number\n\n{GitHubIssue.Version}\n\nSteps to reproduce the error\n\n{reproSteps}\n\nRelevant log output\n\n{GitHubIssue.LogFile}\n\nOther relevant plugins installed\n\n{GitHubIssue.InstalledPlugins}\n\nConfig file\n\n{GitHubIssue.ConfigFile}";

                var issue = new GitHubIssue()
                {
                    Title = title,
                    Body = body
                };

                var json = JsonSerializer.Serialize(issue, BuildTab.jsonSerializerOptions);
                Svc.Log.Info(json);
                _client.DefaultRequestHeaders.Add("User-Agent", "AutoDuty");
                _client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
                _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
                _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

                var content = new StringContent(json, Encoding.UTF8, "application/vnd.github+json");

                // 🔴 指向本 fork:原上游 ffxivcode/AutoDuty 已封存,對封存 repo 開 issue
                // GitHub API 會回 410 Gone,使用者的回報等於丟掉。
                var url = $"https://api.github.com/repos/ffxiv-tc-port/AutoDuty/issues";
                var response = await _client.PostAsync(url, content);

                var responseString = await response.Content.ReadAsStringAsync();
                return responseString;
            }
            catch (Exception ex)
            {
                Svc.Log.Error($"{ex}");
                return null;
            }
        }

        internal static void Dispose() => _client.Dispose();
    }
}
