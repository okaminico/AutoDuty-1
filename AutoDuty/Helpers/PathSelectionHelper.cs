using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoDuty.Helpers
{
    using Data;
    using ECommons.ExcelServices;

    public static class PathSelectionHelper
    {
        public static void AddPathSelectionEntry(uint territoryId)
        {
            if (!Plugin.Configuration.PathSelectionsByPath.ContainsKey(territoryId))
            {
                Dictionary<string, JobWithRole> jobs = [];
                Plugin.Configuration.PathSelectionsByPath.Add(territoryId, jobs);
                if (ContentPathsManager.DictionaryPaths.TryGetValue(territoryId, out ContentPathsManager.ContentPathContainer? container))
                    foreach (Job job in Enum.GetValues<Job>())
                    {
                        // SelectPath 在容器沒有任何可用路徑時回 null(該副本的路徑檔全部解析失敗)。
                        // 舊寫法的 ! 只關掉了編譯器警告,實際會 NRE。沒有路徑就整個迴圈不必再跑。
                        ContentPathsManager.DutyPath? selected = container.SelectPath(out _, job);
                        if (selected == null)
                            break;

                        string path = selected.FileName;
                        jobs.TryAdd(path, JobWithRole.None);
                        jobs[path] |= job.JobToJobWithRole();
                    }

                Plugin.Configuration.Save();
            }
        }

        public static void RebuildDefaultPaths(uint territoryId)
        {
            ContentPathsManager.ContentPathContainer container = ContentPathsManager.DictionaryPaths[territoryId];

            Dictionary<string, JobWithRole>? pathJobConfigs = Plugin.Configuration.PathSelectionsByPath[territoryId];

            JobWithRole jwr = JobWithRole.All;

            foreach (string key in pathJobConfigs.Keys)
                jwr &= ~pathJobConfigs[key];

            foreach (Job job in jwr.ContainedJobs())
            {
                // 同上:沒有可用路徑時 SelectPath 回 null,不能直接解參用。
                ContentPathsManager.DutyPath? selected = container.SelectPath(out _, job);
                if (selected == null)
                    break;

                string path = selected.FileName;
                pathJobConfigs.TryAdd(path, JobWithRole.None);
                pathJobConfigs[path] |= job.JobToJobWithRole();
            }
            Plugin.Configuration.Save();
        }
    }
}
