using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CSRedis;

namespace HSJM.AI.Utility
{
    public class CommonRedisHelper
    {
        public static CSRedisClient client;
        public static void InitRedis(string connectionStr)
        {
            try
            {
                client = new CSRedisClient(connectionStr);
                RedisHelper.Initialization(client);
            }
            catch (Exception ex)
            {
                client = null;
                WriteLogHelper.WriteLogsAsync(ex.ToString(), "Redis初始化异常");
            }
        }

        /// <summary>
        /// 缓存字符串到Redis
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="seconds"></param>
        /// <returns></returns>
        public static bool StringSet(string key, object value, int seconds = 60 * 60 * 24)
        {
            var result = RedisHelper.Set(key, JsonHelper.SerializeObject(value));
            if (seconds > 0)
                RedisHelper.Expire(key, seconds);
            return result;
        }

        /// <summary>
        /// 根据KEY获取字符串
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public static string GetString(string key)
        {
            return RedisHelper.Get(key);
        }

        /// <summary>
        /// 根据KEY获取字符串
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public static T GetString<T>(string key)
        {
            return RedisHelper.Get<T>(key);
        }

        /// <summary>
        /// 设置List
        /// </summary>
        /// <param name="key"></param>
        /// <param name="field"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public static long ListPush<T>(string key, int seconds = 60 * 60 * 24, params T[] value)
        {
            long result = RedisHelper.LPush(key, value);
            if (seconds > 0)
                RedisHelper.Expire(key, seconds);
            return result;
        }

        /// <summary>
        /// 更新List某个下标的值
        /// </summary>
        /// <param name="key"></param>
        /// <param name="field"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public static bool ListSet(string key, long index, object value, int seconds = 60 * 60 * 24)
        {
            var result = RedisHelper.LSet(key, index, value);
            if (seconds > 0)
                RedisHelper.Expire(key, seconds);
            return result;
        }


        /// <summary>
        /// 获取List
        /// </summary>
        /// <param name="key"></param>
        /// <param name="field"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public static List<T> GetList<T>(string key)
        {
            var result = RedisHelper.LRange<T>(key, 0, -1).ToList();
            return result;
        }

        /// <summary>
        /// 获取List指定范围的数据
        /// </summary>
        public static List<T> GetListRange<T>(string key, long start, long stop)
        {
            return RedisHelper.LRange<T>(key, start, stop).ToList();
        }

        /// <summary>
        /// 裁剪List长度
        /// </summary>
        public static bool ListTrim(string key, long start, long stop, int seconds = 60 * 60 * 24)
        {
            var result = RedisHelper.LTrim(key, start, stop);
            if (seconds > 0)
            {
                RedisHelper.Expire(key, seconds);
            }
            return result;
        }

        /// <summary>
        /// 设置Set
        /// </summary>
        /// <param name="key"></param>
        /// <param name="field"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public static long SetAdd<T>(string key, int seconds = 60 * 60 * 24, params T[] value)
        {
            long result = RedisHelper.SAdd(key, value);
            if (seconds > 0)
                RedisHelper.Expire(key, seconds);
            return result;
        }


        /// <summary>
        /// 获取Set
        /// </summary>
        /// <param name="key"></param>
        /// <param name="field"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public static List<T> SetGet<T>(string key)
        {
            var result = RedisHelper.SMembers<T>(key).ToList();
            return result;
        }




        /// <summary>
        /// 设置HASH
        /// </summary>
        /// <param name="key"></param>
        /// <param name="field"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public static bool HashSet(string key, string field, object value, int seconds = 60 * 60 * 24)
        {
            bool result = RedisHelper.HSet(key, field, value);
            if (seconds > 0)
                RedisHelper.Expire(key, seconds);
            return result;
        }

        /// <summary>
        /// 设置HASH 多个值
        /// </summary>
        /// <param name="key"></param>
        /// <param name="field"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public static bool HashSet(string key, object[] value, int seconds = 60 * 60 * 24)
        {
            bool result = RedisHelper.HMSet(key, value);
            if (seconds > 0)
                RedisHelper.Expire(key, seconds);
            return result;

        }

        /// <summary>
        /// 获取HASH
        /// </summary>
        /// <param name="key"></param>
        /// <param name="field"></param>
        /// <returns></returns>
        public static T HashGet<T>(string key, string field)
        {
            return RedisHelper.HGet<T>(key, field);

        }
        /// <summary>
        /// 获取HASH所有值
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public static Dictionary<string, T> GetHashAll<T>(string key)
        {
            return RedisHelper.HGetAll<T>(key);

        }

        /// <summary>
        /// 移除缓存
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public static long DeleteString(params string[] key)
        {
            return RedisHelper.Del(key);
        }

        /// <summary>
        /// 删除一个或多个HASH字段
        /// </summary>
        /// <returns></returns>
        public static long DeleteHash(string key, params string[] fields)
        {
            return RedisHelper.HDel(key, fields);
        }

        /// <summary>
        /// 判断Key是否存在
        /// </summary>
        public static bool Exists(string key)
        {
            return RedisHelper.Exists(key);
        }

        /// <summary>
        /// 更新Key过期时间
        /// </summary>
        public static bool Expire(string key, int seconds)
        {
            return RedisHelper.Expire(key, seconds);
        }

        /// <summary>
        /// 字典信息循环缓存Redis
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="list"></param>
        public static int DictionaryToStringSet<T>(Dictionary<string, T> list)
        {
            int result = 0;
            foreach (var item in list)
            {
                bool isSuccess = RedisHelper.Set(item.Key, JsonHelper.SerializeObject(item.Value));
                if (isSuccess)
                {
                    result++;
                }
            }
            return result;
        }

        /// <summary>
        /// 字典信息循环缓存Redis
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="list"></param>
        public static async Task<int> DictionaryToStringSetAsync<T>(Dictionary<string, T> list)
        {
            int result = 0;
            foreach (var item in list)
            {
                bool isSuccess = await RedisHelper.SetAsync(item.Key, JsonHelper.SerializeObject(item.Value));
                if (isSuccess)
                {
                    result++;
                }
            }

            return result;
        }

        /// <summary>
        /// 写入产线块值到Redis（含索引更新）
        /// </summary>
        /// <param name="line">产线（如A）</param>
        /// <param name="station">工站（如S01）</param>
        /// <param name="step">工步（如ST02）</param>
        /// <param name="action">动作（如AC03）</param>
        /// <param name="block">块（如B04）</param>
        /// <param name="blockValue">块值（支持int、double、string等类型）</param>
        /// <returns>是否写入成功</returns>
        public static bool SaveBlockValue(string line, string station, string step, string action, string block, object blockValue)
        {
            try
            {
                // 1. 拼接各Key
                string blockHashKey = string.Format(RedisKeyConst.BlockHashKey, line, station, step, action, block);
                string lineIndexKey = string.Format(RedisKeyConst.LineIndexSetKey, line);
                string stationIndexKey = string.Format(RedisKeyConst.StationIndexSetKey, station);

                // 2. 写入Hash块值（HSET：不存在则创建，存在则更新value字段）
                RedisHelper.HSet(blockHashKey, RedisKeyConst.BlockValueField, blockValue?.ToString() ?? "");

                // 3. 更新产线索引Set：将工站加入产线的Set（SADD：已存在则忽略，保证唯一性）
                RedisHelper.SAdd(lineIndexKey, station);

                // 4. 更新工站索引Set：将块Hash完整Key加入工站的Set（SADD：已存在则忽略）
                RedisHelper.SAdd(stationIndexKey, blockHashKey);

                return true;
            }
            catch (Exception ex)
            {
                WriteLogHelper.WriteLogsAsync(ex.ToString(), "写入Redis失败");
                return false;
            }
        }

        /// <summary>
        /// 使用SCAN命令灵活查询块值（生产环境推荐，避免阻塞Redis）
        /// </summary>
        /// <param name="query">查询条件</param>
        /// <param name="count">每次扫描的数量</param>
        /// <returns>匹配的块值列表</returns>
        public static async Task<List<BlockValueModel>> GetBlockValuesByScan(BlockQueryCondition query, int count = 100)
        {
            var result = new List<BlockValueModel>();
            try
            {
                // 构建Redis Key匹配模式
                string pattern = string.Format(RedisKeyConst.BlockHashKey,
                    query?.Line ?? "*",
                    query?.Station ?? "*",
                    query?.Step ?? "*",
                    query?.Action ?? "*",
                    query?.ActionAddress ?? "*");

                // 使用SCAN命令迭代查找（不阻塞Redis）
                long cursor = 0;
                do
                {
                    var scanResult = await RedisHelper.ScanAsync(cursor, pattern, count);
                    cursor = scanResult.Cursor;

                    if (scanResult.Items != null && scanResult.Items.Length > 0)
                    {
                        foreach (var key in scanResult.Items)
                        {
                            try
                            {
                                // ✅ 检查 key 类型，只处理 Hash 类型
                                var keyType = await RedisHelper.TypeAsync(key);
                                if (keyType != KeyType.Hash)
                                {
                                    WriteLogHelper.WriteLogsAsync($"跳过非Hash类型的Key: {key}, 类型: {keyType}", "SCAN查询");
                                    continue;
                                }

                                string value = await RedisHelper.HGetAsync(key, RedisKeyConst.BlockValueField);
                                // 解析Key获取各维度信息
                                var model = ParseBlockKey(key, value);
                                if (model != null)
                                {
                                    // 应用值过滤条件（由IsValueMatch决定是否过滤空值）
                                    if (IsValueMatch(model.Value, query))
                                    {
                                        result.Add(model);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                WriteLogHelper.WriteLogsAsync($"处理Key失败: {key}, 错误: {ex.Message}", "SCAN查询");
                                // 继续处理下一个key
                                continue;
                            }
                        }
                    }
                } while (cursor != 0);
            }
            catch (Exception ex)
            {
                WriteLogHelper.WriteLogsAsync(ex.ToString(), "SCAN查询块值失败");
            }
            return result;
        }

        /// <summary>
        /// 解析块Key并生成模型
        /// </summary>
        private static BlockValueModel ParseBlockKey(string key, string value)
        {
            try
            {
                // Key格式：prod:block:A:S01:ST02:AC03:B04
                string[] keyParts = key.Split(':');
                if (keyParts.Length >= 7)
                {
                    return new BlockValueModel
                    {
                        BlockKey = key,
                        Line = keyParts[2],
                        Station = keyParts[3],
                        Step = keyParts[4],
                        Action = keyParts[5],
                        Block = keyParts[6],
                        Value = value  // 保持原始字符串格式
                    };
                }
            }
            catch (Exception ex)
            {
                WriteLogHelper.WriteLogsAsync($"解析Key失败：{key}, {ex}", "ParseBlockKey");
            }
            return null;
        }

        /// <summary>
        /// 判断值是否匹配查询条件
        /// </summary>
        /// <param name="value">实际值（字符串格式）</param>
        /// <param name="query">查询条件</param>
        /// <returns>是否匹配</returns>
        private static bool IsValueMatch(string value, BlockQueryCondition query)
        {
            if (query == null) return true;

            // 检查是否有任何值过滤条件
            bool hasValueFilter = !string.IsNullOrEmpty(query.StringValue) || 
                                  query.Value.HasValue || 
                                  query.MinValue.HasValue || 
                                  query.MaxValue.HasValue;

            // 如果没有任何值过滤条件，返回true（允许所有值，包括空值）
            if (!hasValueFilter)
            {
                return true;
            }

            // 如果有值过滤条件，但实际值为空，返回false
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            // 字符串精确匹配（优先级最高）
            if (!string.IsNullOrEmpty(query.StringValue))
            {
                return string.Equals(value, query.StringValue, StringComparison.Ordinal);
            }

            // 数值精确匹配
            if (query.Value.HasValue)
            {
                // 尝试将字符串值转换为double进行比较
                if (double.TryParse(value, out double doubleValue))
                {
                    return Math.Abs(doubleValue - query.Value.Value) < 0.0001;
                }
                return false;
            }

            // 范围匹配（仅对数值类型有效）
            if (query.MinValue.HasValue || query.MaxValue.HasValue)
            {
                if (double.TryParse(value, out double doubleValue))
                {
                    if (query.MinValue.HasValue && doubleValue < query.MinValue.Value)
                    {
                        return false;
                    }

                    if (query.MaxValue.HasValue && doubleValue > query.MaxValue.Value)
                    {
                        return false;
                    }
                    return true;
                }
                // 如果不是数值类型，则范围过滤无效
                return false;
            }

            return true;
        }

        /// <summary>
        /// 批量写入步骤值（Pipeline优化，高频采集必用）
        /// </summary>
        public static async Task<bool> BatchSaveBlockValuesAsync(List<(string Line, string Station, string Step, string Action, string Block, object Value)> dataList)
        {
            try
            {
                if (dataList == null || dataList.Count == 0) return false;

                // CSRedis 使用 Pipeline 批量执行
                var pipe = RedisHelper.StartPipe();
                foreach (var data in dataList)
                {
                    string blockHashKey = string.Format(RedisKeyConst.BlockHashKey, data.Line, data.Station, data.Step, data.Action, data.Block);
                    string lineIndexKey = string.Format(RedisKeyConst.LineIndexSetKey, data.Line);
                    string stationIndexKey = string.Format(RedisKeyConst.StationIndexSetKey, data.Station);

                    // 将命令加入管道
                    pipe.HSet(blockHashKey, RedisKeyConst.BlockValueField, data.Value?.ToString() ?? "");
                    pipe.SAdd(lineIndexKey, data.Station);
                    pipe.SAdd(stationIndexKey, blockHashKey);
                }
                // 执行管道中所有命令
                pipe.EndPipe();
                return true;
            }
            catch (Exception ex)
            {
                WriteLogHelper.WriteLogsAsync(ex.ToString(), "批量写入块值失败");
                return false;
            }
        }


        /// <summary>
        /// 异步写入产线步骤值到Redis（含索引更新）
        /// </summary>
        public static async Task<bool> SaveBlockValueAsync(string line, string station, string step, string action, string block, object blockValue)
        {
            try
            {
                string blockHashKey = string.Format(RedisKeyConst.BlockHashKey, line, station, step, action, block);
                string lineIndexKey = string.Format(RedisKeyConst.LineIndexSetKey, line);
                string stationIndexKey = string.Format(RedisKeyConst.StationIndexSetKey, station);

                await RedisHelper.HSetAsync(blockHashKey, RedisKeyConst.BlockValueField, blockValue?.ToString() ?? "");
                await RedisHelper.SAddAsync(lineIndexKey, station);
                await RedisHelper.SAddAsync(stationIndexKey, blockHashKey);
                return true;
            }
            catch (Exception ex)
            {
                WriteLogHelper.WriteLogsAsync(ex.ToString(), "异步写入Redis失败");
                return false;
            }
        }

        /// <summary>
        /// Redis Key命名规范常量（产线步骤值场景）
        /// </summary>
        public static class RedisKeyConst
        {
            /// <summary>
            /// 块值Hash存储Key格式：prod:block:{产线}:{工站}:{工步}:{动作}:{块}
            /// </summary>
            public const string BlockHashKey = "prod:block:{0}:{1}:{2}:{3}:{4}";

            /// <summary>
            /// 产线索引Set Key格式：prod:index:line:{产线}
            /// </summary>
            public const string LineIndexSetKey = "prod:index:line:{0}";

            /// <summary>
            /// 工站索引Set Key格式：prod:index:station:{工站}
            /// </summary>
            public const string StationIndexSetKey = "prod:index:station:{0}";

            /// <summary>
            /// Hash中存储块值的Field名
            /// </summary>
            public const string BlockValueField = "value";
        }

        /// <summary>
        /// 步骤值查询条件（所有参数可选，支持自由组合）
        /// </summary>
        public class BlockQueryCondition
        {
            /// <summary>
            /// 产线（可选，null表示不限制）
            /// </summary>
            public string Line { get; set; }

            /// <summary>
            /// 工站（可选，null表示不限制）
            /// </summary>
            public string Station { get; set; }

            /// <summary>
            /// 工步（可选，null表示不限制）
            /// </summary>
            public string Step { get; set; }

            /// <summary>
            /// 动作（可选，null表示不限制）
            /// </summary>
            public string Action { get; set; }

            /// <summary>
            /// 动作地址（可选，null表示不限制）
            /// </summary>
            public string ActionAddress { get; set; }

            /// <summary>
            /// 精确值匹配（可选，null表示不限制。仅对数值类型有效）
            /// </summary>
            public double? Value { get; set; }

            /// <summary>
            /// 最小值（可选，null表示不限制。大于等于此值，仅对数值类型有效）
            /// </summary>
            public double? MinValue { get; set; }

            /// <summary>
            /// 最大值（可选，null表示不限制。小于等于此值，仅对数值类型有效）
            /// </summary>
            public double? MaxValue { get; set; }

            /// <summary>
            /// 字符串精确匹配（可选，null表示不限制。用于匹配非数值类型的值）
            /// </summary>
            public string StringValue { get; set; }
        }

        /// <summary>
        /// 产线步骤值查询返回模型
        /// </summary>
        public class BlockValueModel
        {
            /// <summary>
            /// 块的完整Redis Key
            /// </summary>
            public string BlockKey { get; set; }

            /// <summary>
            /// 产线
            /// </summary>
            public string Line { get; set; }

            /// <summary>
            /// 工站
            /// </summary>
            public string Station { get; set; }

            /// <summary>
            /// 工步
            /// </summary>
            public string Step { get; set; }

            /// <summary>
            /// 动作
            /// </summary>
            public string Action { get; set; }

            /// <summary>
            /// 块
            /// </summary>
            public string Block { get; set; }

            /// <summary>
            /// 块值（原始字符串格式，调用方根据需要自行转换为int、double等类型）
            /// </summary>
            public string Value { get; set; }

            /// <summary>
            /// 尝试将值转换为double类型
            /// </summary>
            public double? GetDoubleValue()
            {
                if (double.TryParse(Value, out double result))
                    return result;
                return null;
            }

            /// <summary>
            /// 尝试将值转换为int类型
            /// </summary>
            public int? GetIntValue()
            {
                if (int.TryParse(Value, out int result))
                    return result;
                return null;
            }

            /// <summary>
            /// 尝试将值转换为bool类型
            /// </summary>
            public bool? GetBoolValue()
            {
                if (bool.TryParse(Value, out bool result))
                    return result;
                return null;
            }
        }
    }
}
