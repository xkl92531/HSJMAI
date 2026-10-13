using MAF.DEMO.Core;
using MAF.DEMO.Utility.LoggerHelper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Timers;
using Timer = System.Timers.Timer;

namespace MAF.DEMO.Utility
{

    /// <summary>
    /// TPL任务并行库记录日志方法类（详情请参阅TPL 任务并行库 ）
    /// 适合频繁记录日志，机制为：插入时插入缓存中，满足一定条件，比如10条或者2s时，触发提交。
    /// </summary>
    public class TPLLogger
    {
        public static TPLLogOptions options=new TPLLogOptions();
        private static ActionBlock<LogModel> logBlock;
        private static BatchBlock<LogModel> batchLogModelData = new BatchBlock<LogModel>(options.HandleDataSize);//数据缓存块
        ExecutionDataflowBlockOptions executionDataflowBlockOptions = new ExecutionDataflowBlockOptions() { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism };
        private static Timer timer;//定时处理缓存块消息
        private static bool insertStatus = false;//插入状态
        private static DateTime lastInsertTime = DateTime.Now;//上一次插入时间
        private static BroadcastBlock<LogModel[]> broadCastBlock;//数据分发处理块
        DataflowLinkOptions linkOptions = new DataflowLinkOptions { PropagateCompletion = true, };//并行任务
        private static ActionBlock<LogModel[]> saveData;//保存处理块
        public TPLLogger()
        {
            InitTPL();
        }
        private void InitTPL()
        {
            HanderLog();//处理日志模型入缓存块
            SendBatchBlock();//发送缓存块
            SaveLogBlock();//保存数据
            TimeHandle();
        }

        public async Task InsertAsync(LogModel logModel)
        {
            await SendToTPLAsync(logModel);
        }

        private void TimeHandle()
        {
            #region 定时触发处理未满缓存区的信息
            timer = new Timer(options.HandleInterval);
            timer.Elapsed += new ElapsedEventHandler(delegate
            {
                if (insertStatus == false && (DateTime.Now - lastInsertTime).TotalMilliseconds > options.HandleTimeOut)
                {
                    insertStatus = true;
                    batchLogModelData.TriggerBatch();
                }

            });
            timer.Interval = 1;
            timer.Start();
            #endregion
        }

        /// <summary>
        /// 推送日志入缓存块
        /// </summary>
        private void HanderLog()
        {
            logBlock = new ActionBlock<LogModel>(log =>
            {
                batchLogModelData.Post(log);

            }, executionDataflowBlockOptions);
        }

        /// <summary>
        /// 发送数据到TPL工作流处理
        /// </summary>
        /// <param name="bytes"></param>
        /// <returns></returns>
        private async Task SendToTPLAsync(LogModel logModel)
        {
            await logBlock.SendAsync(logModel);
        }

        /// <summary>
        /// 分发缓存块消息
        /// </summary>
        private void SendBatchBlock()
        {
            if (broadCastBlock == null)
            {
                broadCastBlock = new BroadcastBlock<LogModel[]>(data =>
                {
                    lastInsertTime = DateTime.Now;
                    insertStatus = true;
                    return data;
                });
                batchLogModelData.LinkTo(broadCastBlock, linkOptions);
            }
        }

        /// <summary>
        ///  保存数据
        /// </summary>
        /// <returns></returns>
        private void SaveLogBlock()
        {
            if (saveData == null)
            {
                saveData = new ActionBlock<LogModel[]>(logArray =>
                {
                    SaveLogBatch(logArray.ToList());
                }, executionDataflowBlockOptions);
                broadCastBlock.LinkTo(saveData, linkOptions);//分发保存MQ
            }
        }

        private void SaveLogBatch(List<LogModel> logModelList)
        {
            foreach (var logModel in logModelList)
            {
                MyLogger.Logger.Information(logModel.LogMessage);
            }
        }
    }
}
