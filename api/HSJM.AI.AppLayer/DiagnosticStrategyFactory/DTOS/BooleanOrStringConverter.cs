using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS
{
    /// <summary>
    /// 布尔值或字符串的 JSON 转换器
    /// 支持将布尔值（true/false）自动转换为字符串（"True"/"False"）
    /// </summary>
    public class BooleanOrStringConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // 根据 JSON Token 类型处理
            switch (reader.TokenType)
            {
                case JsonTokenType.True:
                    return "True";
                
                case JsonTokenType.False:
                    return "False";
                
                case JsonTokenType.String:
                    return reader.GetString();
                
                case JsonTokenType.Number:
                    // 如果是数字，转换为字符串
                    if (reader.TryGetInt32(out int intValue))
                        return intValue.ToString();
                    if (reader.TryGetDouble(out double doubleValue))
                        return doubleValue.ToString();
                    return reader.GetString();
                
                default:
                    throw new JsonException($"无法将 {reader.TokenType} 转换为字符串");
            }
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            // 写入时直接输出字符串
            writer.WriteStringValue(value);
        }
    }
}
