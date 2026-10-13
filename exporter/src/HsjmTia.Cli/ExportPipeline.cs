using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using HsjmTia.Core.Analyze;
using HsjmTia.Core.Cli;
using HsjmTia.Core.Discovery;
using HsjmTia.Core.Manifest;
using HsjmTia.Core.Upload;
using HsjmTia.Openness;

namespace HsjmTia.Cli
{
    public static class ExportPipeline
    {
        public static int Run(CliArguments args, OpennessInstallation installation)
        {
            var runtime = AnalyzerRuntime.Resolve(AppDomain.CurrentDomain.BaseDirectory);

            ExportRunResult result;
            try
            {
                result = ExportOrchestrator.Run(args.ProjectPath, installation, args.OutputRoot, runtime);
            }
            catch (NoCpusFoundException ex)
            {
                // 唯一在连接/枚举阶段就优雅终止的情况；其它异常（COM 连接失败、工程文件缺失等）
                // 原样冒泡到 Main 之外，与改造前一致，不在这里额外兜底。
                Console.Error.WriteLine(ex.Message);
                return 4;
            }

            Report(result);

            var anyGaps = result.Gaps.Count > 0;

            if (args.Upload)
            {
                anyGaps |= !Upload(args, result.Packages);
            }

            return anyGaps ? 1 : 0;
        }

        private static void Report(ExportRunResult result)
        {
            Console.WriteLine($"工程 {result.ProjectName} 共 {result.Plcs.Count} 个 PLC：");
            foreach (var plc in result.Plcs)
            {
                Console.WriteLine($"  - {plc.Name}  IP={plc.Ip ?? "(未组态)"}");
            }

            Console.WriteLine();
            Console.WriteLine($"本地解析 {result.AnalyzeSucceeded}/{result.AnalyzeTotal} 成功");
            Console.WriteLine($"成功打包 {result.Packages.Count}/{result.Plcs.Count} 个 PLC");

            if (result.Gaps.Count == 0)
            {
                Console.WriteLine("无缺口");
                return;
            }

            Console.WriteLine($"缺口 {result.Gaps.Count} 条：");
            foreach (var gap in result.Gaps)
            {
                Console.WriteLine($"  [{gap.Kind}] {gap.Target} — {gap.Detail}");
            }
        }

        /// <summary>返回 true 表示全部上传成功。</summary>
        private static bool Upload(CliArguments args, IReadOnlyList<ExportPackage> packages)
        {
            if (string.IsNullOrWhiteSpace(args.Server))
            {
                Console.Error.WriteLine("--upload 需要同时指定 --server");
                return false;
            }

            var ok = true;
            using (var http = new HttpClient { BaseAddress = new Uri(args.Server.TrimEnd('/') + "/") })
            {
                var client = new BackendClient(http);
                IReadOnlyList<BackendProject> projects;
                try
                {
                    client.LoginAsync(args.User, args.Password).GetAwaiter().GetResult();
                    projects = client.GetProjectsAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    // 到这一步所有导出都已经成功落盘，只有上传这一步受影响——不能让登录/取项目列表失败
                    // 把整个进程带崩，否则用户会误以为连导出本身都失败了。
                    Console.Error.WriteLine(
                        $"连接后端失败（{args.Server}）：{ex.GetType().Name}: {ex.Message}");
                    return false;
                }

                foreach (var package in packages)
                {
                    var plc = package.Plc;
                    var zipPath = package.ZipPath;
                    var ip = (plc.Ip ?? string.Empty).Trim();
                    if (!args.PlcTargets.TryGetValue(ip, out var projectId))
                    {
                        Console.Error.WriteLine(
                            $"[{plc.Name}] 未指定目标项目（缺 --plc {plc.Name}@{ip}=<projectId>），跳过上传");
                        ok = false;
                        continue;
                    }

                    var project = projects.FirstOrDefault(p => p.Id == projectId);
                    if (project == null)
                    {
                        Console.Error.WriteLine($"[{plc.Name}] 后端不存在 projectId={projectId}，跳过上传");
                        ok = false;
                        continue;
                    }

                    // CLI 是无人值守，不能交互确认：任何需要确认的判定一律拒绝上传
                    var check = UploadTargetValidator.Validate(plc, project);
                    if (check.RequiresConfirmation)
                    {
                        Console.Error.WriteLine($"[{plc.Name}] 拒绝上传：{check.Message}");
                        ok = false;
                        continue;
                    }

                    // C2：--full 会让后端把"zip 里没有、DB 里有"的块当成已删除整块硬删。
                    // 知产保护 / 导出失败而缺席这次导出的块不是"真的被删了"，无人值守场景下
                    // 绝不能替工程师做这个判断——同样的道理适用于 IP 不一致：拒绝，不猜测。
                    if (args.FullProjectUpdate)
                    {
                        var unsafeGaps = FullUpdateSafety.FindUnsafeGaps(package.Gaps);
                        if (unsafeGaps.Count > 0)
                        {
                            Console.Error.WriteLine(
                                $"[{plc.Name}] 拒绝全项目更新上传：以下 {unsafeGaps.Count} 个块本次未包含在导出里，" +
                                "--full 会让后端把它们当成已删除并硬删其全部历史数据：");
                            foreach (var gap in unsafeGaps)
                            {
                                Console.Error.WriteLine($"    - [{gap.Kind}] {gap.Target} — {gap.Detail}");
                            }
                            ok = false;
                            continue;
                        }
                    }

                    var taskId = client.StartUploadAsync(zipPath, projectId, args.FullProjectUpdate)
                        .GetAwaiter().GetResult();
                    Console.WriteLine($"[{plc.Name}] 已提交，taskId={taskId}");

                    if (!PollUploadUntilDone(client, plc.Name, taskId))
                    {
                        ok = false;
                    }
                }
            }

            return ok;
        }

        /// <summary>
        /// 轮询上传进度直到完成。返回 true 表示成功完成（UploadStatus.Completed）。
        /// UploadProgressDto 没有 Stage/Percent/IsCompleted 这几个虚构字段——真实形状是
        /// TaskId/Status(枚举)/Percentage/Message/CurrentFile/ProcessedFiles/TotalFiles/ErrorDetails，
        /// IsCompleted 是按 Status==Completed||Status==Failed 计算出来的；Failed 必须算作上传失败，
        /// 不能因为"轮询循环正常结束"就当成功处理。
        /// </summary>
        private static bool PollUploadUntilDone(BackendClient client, string plcName, string taskId)
        {
            while (true)
            {
                Thread.Sleep(2000);

                UploadProgressDto progress;
                try
                {
                    progress = client.GetProgressAsync(taskId).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    // GetProgressAsync 现在对"任务真的查不到/已过期"之外的一切失败都抛异常
                    // （瞬时后端错误、网络故障等），这是正确的——但这里是无人值守的多 PLC 批量
                    // 上传循环：让异常原样冒出去会带崩整个 Main，PLC 2..N 永远轮不到。
                    // 只让这一个 PLC 的上传记为失败，循环继续跑下一个 PLC。
                    Console.Error.WriteLine(
                        $"[{plcName}] 查询上传进度失败（taskId={taskId}）：{ex.GetType().Name}: {ex.Message}");
                    return false;
                }

                if (progress == null)
                {
                    Console.Error.WriteLine($"[{plcName}] 任务不存在或已过期");
                    return false;
                }

                Console.WriteLine($"  {progress.Percentage}% {progress.Status} {progress.Message}");
                if (!progress.IsCompleted)
                {
                    continue;
                }

                if (progress.Status == UploadStatus.Failed)
                {
                    Console.Error.WriteLine(
                        $"[{plcName}] 上传失败：{progress.ErrorDetails ?? progress.Message}");
                    return false;
                }

                return true;
            }
        }
    }
}
