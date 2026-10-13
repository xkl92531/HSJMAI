using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CSRedis;

namespace MAF.DEMO.Utility
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
    }
}
