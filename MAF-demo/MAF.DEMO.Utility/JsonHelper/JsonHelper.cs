
namespace MAF.DEMO.Utility
{ /// <summary>
  /// json帮助类，序列化，反序列化
  /// </summary>

    public static class JsonHelper
    {
        /// <summary>
        /// 对象转字符串
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="message"></param>
        /// <returns></returns>
        public static string SerializeObject<T>(T message, bool isUseTextJson = false)
        {
            if (isUseTextJson)
            {
                return TextJsonHelper.SerializeObject<T>(message);
            }
            return NewtonsoftJsonHelper.SerializeObject<T>(message);
        }

       

        /// <summary>
        /// 字符串转对象
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="message"></param>
        /// <returns></returns>
        public static T DeserializeObject<T>(string message, bool isUseTextJson = false)  
        {
            if (isUseTextJson)
            {
                return TextJsonHelper.DeserializeObject<T>(message);
            }
            return NewtonsoftJsonHelper.DeserializeObject<T>(message);
        }

        /// <summary>
        /// 字符串转对象
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="message"></param>
        /// <returns></returns>
        public static string DeserializeObject(string message)
        {
            return message;
        }
    }
}
