using MAF.DEMO.Utility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Extensions;
using OfficeOpenXml.FormulaParsing.Excel.Functions.DateTime;
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace MAF.DEMO.Controllers
{
    [Route("Enum")]
    [ApiController]
    [DisplayName("枚举")]
    public class EnumController : ControllerBase
    {
        /// <summary>
        /// 通过枚举类型获取枚举值集合
        /// </summary>
        /// <param name="enumName"></param>
        /// <returns></returns>
        [HttpGet("/Enum/GetList")]
        public async Task<dynamic> GetList([FromQuery] string enumName)
        {
            var enumType = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => a.GetTypes()).Where(it => it.Name == enumName && it.IsEnum).FirstOrDefault();
            //// 获取枚举的Key和描述
            return await Task.Run(() =>
                   EnumTypeExtensions.GetEnumDictionary(enumType)
                   .Select(x => new
                   {
                       Value = x.Key,
                       Label = x.Value
                   }));
        }

    }
}
