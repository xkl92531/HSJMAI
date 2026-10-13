using Newtonsoft.Json;
using System;

namespace HSJM.AI.Core
{
    /// <summary>
    /// 雪花 ID 安全序列化：long 值绝对值超过 JS Number.MAX_SAFE_INTEGER(2^53-1) 时序列化为字符串，
    /// 避免前端 JSON.parse 丢精度导致回传的 Id 查不到数据；小数值仍输出数字，兼容既有前端。
    /// 读取方向兼容数字与字符串两种形态。
    /// </summary>
    public sealed class LongSnowflakeSafeJsonConverter : JsonConverter
    {
        private const long JsSafeLimit = 9007199254740991L;

        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(long) || objectType == typeof(long?);
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            if (value is null)
            {
                writer.WriteNull();
                return;
            }

            var typed = (long)value;
            if (Math.Abs(typed) > JsSafeLimit)
            {
                writer.WriteValue(typed.ToString());
            }
            else
            {
                writer.WriteValue(typed);
            }
        }

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            switch (reader.TokenType)
            {
                case JsonToken.Integer:
                    return Convert.ToInt64(reader.Value);
                case JsonToken.String:
                    var text = (string?)reader.Value;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return objectType == typeof(long?) ? null : 0L;
                    }

                    return long.Parse(text.Trim());
                case JsonToken.Null:
                    return objectType == typeof(long?) ? null : 0L;
                default:
                    throw new JsonSerializationException($"无法将 {reader.TokenType} 转换为 long：{reader.Value}");
            }
        }
    }

    /// <summary>
    /// System.Text.Json 版（SignalR AddJsonProtocol 用）：雪花 ID 超过 JS 安全整数时输出字符串。
    /// </summary>
    public sealed class LongSnowflakeSafeSystemTextJsonConverter : System.Text.Json.Serialization.JsonConverter<long>
    {
        private const long JsSafeLimit = 9007199254740991L;

        public override long Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                System.Text.Json.JsonTokenType.Number => reader.GetInt64(),
                System.Text.Json.JsonTokenType.String => long.Parse(reader.GetString()!),
                _ => throw new System.Text.Json.JsonException($"无法将 {reader.TokenType} 转换为 long")
            };
        }

        public override void Write(System.Text.Json.Utf8JsonWriter writer, long value, System.Text.Json.JsonSerializerOptions options)
        {
            if (Math.Abs(value) > JsSafeLimit)
            {
                writer.WriteStringValue(value.ToString());
            }
            else
            {
                writer.WriteNumberValue(value);
            }
        }
    }
}
