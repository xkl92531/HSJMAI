
using System.Linq.Expressions;
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/1/26
using HSJM.AI.IDbService;
using HSJM.AI.Core;
using HSJM.AI.Utility;

namespace HSJM.AI.DbService
{
    public class PLC_Parser_TemplateService : AbstractBaseService<PLC_Parser_TemplateEntity>, IPLC_Parser_TemplateService, IAutoInject
    {
        /// <summary>
        /// 异步根据条件软删
        /// </summary>
        /// <param name="whereExpression"></param>
        /// <returns></returns>
        public async Task<bool> SoftDeleteAsync(Expression<Func<PLC_Parser_TemplateEntity, bool>> whereExpression)
        {
            var result = false;
            var delDatas = await GetListAsync(whereExpression);
            if (delDatas.IsListNullOrEmpty() == false)
            {
                delDatas.ForEach(x => x.IsDeleted = true);
                int rows = await UpdateRangeAsync(delDatas);
                result = rows > 0;
            }
            return result;
        }

        /// <summary>
        /// 异步软删根据id删除
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        public async Task<bool> SoftDeleteByIdAsync(object id)
        {
            var result = false;
            var delData = GetById(id);
            if (delData != null)
            {
                delData.IsDeleted = true;
                result = await UpdateAsync(delData);
            }
            return result;
        }

        /// <summary>
        /// 异步软删根据ids删除
        /// </summary>
        /// <param name="listId">主键列表</param>
        /// <returns></returns>
        public async Task<bool> SoftDeleteByIdsAsync(List<long> listId)
        {
            var result = false;
            var delDatas = await GetListByIdsAsync(listId);
            if (delDatas.IsListNullOrEmpty() == false)
            {
                delDatas.ForEach(x => x.IsDeleted = true);
                int rows = await UpdateRangeAsync(delDatas);
                result = rows > 0;
            }
            return result;
        }

        /// <summary>
        /// 根据品牌和格式查找最优解析器模版
        /// 优先级规则：IsActive=true > Priority高 > SuccessRate高
        /// </summary>
        public async Task<PLC_Parser_TemplateEntity?> GetBestTemplateAsync(string plcBrand, string fileFormat)
        {
            var templates = await GetListAsync(t =>
                t.IsDeleted == false &&
                t.PlcBrand == plcBrand &&
                t.FileFormat == fileFormat &&
                t.IsActive == true);

            if (templates.IsListNullOrEmpty())
            {
                return null;
            }

            // 按优先级和成功率排序
            return templates
                .OrderByDescending(t => t.Priority ?? 0)
                .ThenByDescending(t => t.SuccessCount.HasValue && t.UsageCount.HasValue && t.UsageCount > 0
                    ? (decimal)t.SuccessCount / t.UsageCount
                    : 0)
                .FirstOrDefault();
        }
    }
}
