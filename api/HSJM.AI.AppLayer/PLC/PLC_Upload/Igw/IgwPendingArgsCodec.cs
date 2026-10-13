using System;
using System.Linq;
using System.Text.Json;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload.Igw
{
    /// <summary>
    /// 待补跑注册范围 ↔ JSON（M-1，写入 ai_plc_igwregistrationstate.pendingargsjson）。
    /// 不含上传任务 ID（进度槽在进程内存，重启后无意义）。
    /// </summary>
    public static class IgwPendingArgsCodec
    {
        private sealed class Dto
        {
            public string[] Changed { get; set; }
            public string[] Deleted { get; set; }
            public bool IoRefreshed { get; set; }
            public bool Full { get; set; }
        }

        /// <summary>范围 → JSON（null → null；不含任务 ID）</summary>
        public static string Serialize(IgwRegistrationArgs args)
        {
            if (args == null)
            {
                return null;
            }
            return JsonSerializer.Serialize(new Dto
            {
                Changed = (args.ChangedBlocks ?? Array.Empty<string>()).ToArray(),
                Deleted = (args.DeletedBlocks ?? Array.Empty<string>()).ToArray(),
                IoRefreshed = args.IoRefreshed,
                Full = args.Full,
            });
        }

        /// <summary>两份范围 JSON 取并集（写入其它机器所属记录时用；某一份非法按缺失处理，都非法 → 返回 <paramref name="json"/>）</summary>
        public static string Merge(string existingJson, string json) =>
            Serialize(IgwRegistrationArgs.Union(Deserialize(existingJson), Deserialize(json))) ?? json;

        /// <summary>JSON → 范围（空 / 非法 → null）</summary>
        public static IgwRegistrationArgs Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }
            try
            {
                var dto = JsonSerializer.Deserialize<Dto>(json);
                return dto == null
                    ? null
                    : new IgwRegistrationArgs(dto.Changed ?? Array.Empty<string>(), dto.Deleted ?? Array.Empty<string>(),
                        dto.IoRefreshed, dto.Full, null);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
