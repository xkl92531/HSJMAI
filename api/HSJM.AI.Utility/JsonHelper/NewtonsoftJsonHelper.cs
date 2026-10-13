using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Text.Json;
using System;

namespace HSJM.AI.Utility
{
    /// <summary>
    /// json帮助类，序列化，反序列化
    /// </summary>
    public static class NewtonsoftJsonHelper
    {

        /// <summary>
        /// 对象转字符串
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="message"></param>
        /// <returns></returns>
        public static string SerializeObject<T>(T message)
        {

            //Newtonsoft.Json的用法
            var settings = new JsonSerializerSettings
            {
                ContractResolver = new DefaultContractResolver
                {
                    NamingStrategy = new CamelCaseNamingStrategy
                    {
                        ProcessDictionaryKeys = false,
                        OverrideSpecifiedNames = true
                    }
                }
            };
            settings.Converters.Add(new JsonElementConverter());
            var str_Message = message is string ? message.ToString() : JsonConvert.SerializeObject(message, Formatting.None, settings);
            return str_Message;
        }

        /// <summary>
        /// 字符串转对象
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="message"></param>
        /// <returns></returns>
        public static T DeserializeObject<T>(string message)
        {
            //Newtonsoft.Json的用法

            var obj = JsonConvert.DeserializeObject<T>(message);
            return obj;

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

    /// <summary>
    /// 支持 System.Text.Json.JsonElement 的 Newtonsoft 转换器
    /// </summary>
    public class JsonElementConverter : Newtonsoft.Json.JsonConverter<JsonElement>
    {
        public override void WriteJson(JsonWriter writer, JsonElement value, Newtonsoft.Json.JsonSerializer serializer)
        {
            writer.WriteRawValue(value.GetRawText());
        }

        public override JsonElement ReadJson(JsonReader reader, Type objectType, JsonElement existingValue, bool hasExistingValue, Newtonsoft.Json.JsonSerializer serializer)
        {
            throw new NotImplementedException("Deserialization of JsonElement via Newtonsoft is not supported/needed.");
        }
    }
}
