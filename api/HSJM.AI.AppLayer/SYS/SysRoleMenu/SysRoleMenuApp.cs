
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 09:41:27
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System.Linq.Expressions;
using System;
using HSJM.AI.IDbService;
using SqlSugar;
using HSJM.AI.Utility;
using HSJM.AI.Core;
using HSJM.AI.AppLayer;
using HSJM.AI.AppLayer.SYS.ViewObject;
using HSJM.AI.AppLayer.SYS.DTOS;
using HSJM.AI.NPOIHelper;
using System.IO;
using Microsoft.AspNetCore.Http;
using HSJM.AI.AppLayer.SYS.SysRoleMenu.DTOS;
using Newtonsoft.Json;
namespace HSJM.AI.AppLayer
{
    public class SysRoleMenuApp : ISelfScopedAutoInject
    {
        private readonly ISysRoleMenuService sysRoleMenuService;
        private readonly ISysRoleService sysRoleService;
        private readonly ISysMenuService sysMenuService;
        private readonly ISysMenuButtonService sysMenuButtonService;
        private readonly ISysSystemService sysSystemService;

        public SysRoleMenuApp(ISysRoleMenuService sysRoleMenuService, ISysRoleService sysRoleService, ISysMenuService sysMenuService, ISysMenuButtonService sysMenuButtonService, ISysSystemService sysSystemService)
        {
            this.sysRoleMenuService = sysRoleMenuService;
            this.sysRoleService = sysRoleService;
            this.sysMenuService = sysMenuService;
            this.sysMenuButtonService = sysMenuButtonService;
            this.sysSystemService = sysSystemService;
        }

        /// <summary>
        /// 根据主键获取单个实体
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        public async Task<SysRoleMenuEntity> GetAsync(long id)
        {
            return await sysRoleMenuService.GetByIdAsync(id).ConfigureAwait(false);
        }


        /// <summary>
        /// 查询数据(分页) 返回指定实体T
        /// </summary>
        /// <param name="page">页码</param>
        /// <param name="size">每页数量</param>
        /// <param name="conditionDTO">查询条件类</param>
        /// <param name="orderByField">排序字段</param>
        /// <param name="isDesc">Desc</param>
        /// <returns></returns>
        public async Task<QueryPagedResponseModel<SysRoleMenuEntity>> GetListPagedAsync(int page, int size, SysRoleMenuConditionDTO conditionDTO = null, string orderByField = "Id", bool isDesc = true)
        {
            var expression = PackageExpression(conditionDTO);

            int total = 0;
            var result = sysRoleMenuService.GetListPageOrderBy(expression, page, size, orderByField.CreateOrderByExpression<SysRoleMenuEntity>(), isDesc ? OrderByType.Desc : OrderByType.Asc, ref total);
            return new QueryPagedResponseModel<SysRoleMenuEntity>()
            {
                Total = total,
                Data = result
            };
        }

        /// <summary>
        /// 查询数据(不分页) 返回指定实体T
        /// </summary>
        /// <param name="conditionDTO">查询条件类</param>
        /// <param name="orderByField">排序字段</param>
        /// <param name="isDesc">Desc</param>
        /// <returns></returns>
        public async Task<List<SysRoleMenuEntity>> GetListAsync(SysRoleMenuConditionDTO conditionDTO, string orderByField = "Id", bool isDesc = true)
        {
            var expression = PackageExpression(conditionDTO);
            var result = sysRoleMenuService.GetList(expression, orderByField.CreateOrderByExpression<SysRoleMenuEntity>(), isDesc ? OrderByType.Desc : OrderByType.Asc);
            return result;
        }

        /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        public async Task<List<SysRoleMenuEntity>> GetListByIdsAsync(List<long> ids)
        {
            if (ids.IsListNullOrEmpty())
            {
                return new List<SysRoleMenuEntity>();
            }
            return await sysRoleMenuService.GetListByIdsAsync(ids);
        }

        /// <summary>
        /// 获取最后一条
        /// </summary>
        /// <returns></returns>
        public async Task<SysRoleMenuEntity> GetFirstAsync(SysRoleMenuConditionDTO conditionDTO)
        {
            var expression = PackageExpression(conditionDTO);
            return sysRoleMenuService.GetFirst(expression, x => x.CreateTime, OrderByType.Desc);
        }

        /// <summary>
        /// 插入单个实体
        /// </summary>
        /// <param name="insertDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<long>> InsertAsync(SysRoleMenuInsertDTO insertDto)
        {
            HttpResponseResultModel<long> httpResponseResultModel = new HttpResponseResultModel<long>();
            var entity = insertDto.MapTo<SysRoleMenuEntity>();
            httpResponseResultModel.IsSuccess = await sysRoleMenuService.InsertAsync(entity).ConfigureAwait(false);
            httpResponseResultModel.BackResult = entity.Id;
            return httpResponseResultModel;
        }



        /// <summary>
        /// 批量插入实体
        /// </summary>
        /// <param name="insertDtoList">对应实体数据传输对象集合</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> InsertBatchAsync(List<SysRoleMenuInsertDTO> insertDtoList)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entityList = insertDtoList.MapToList<SysRoleMenuEntity>();
            httpResponseResultModel.IsSuccess = await sysRoleMenuService.InsertRangeAsync(entityList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }


        /// <summary>
        /// 根据主键更新实体
        /// </summary>
        /// <param name="id">主键</param>
        /// <param name="updateDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> UpdateAsync(long id, SysRoleMenuUpdateDTO updateDto)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entity = updateDto.MapTo<SysRoleMenuEntity>();
            entity.Id = id;
            httpResponseResultModel.IsSuccess = await sysRoleMenuService.UpdateAsync(entity);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }


        /// <summary>
        /// 根据根据主键删除
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> DeleteAsync(long id)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            httpResponseResultModel.IsSuccess = await sysRoleMenuService.SoftDeleteByIdAsync(id);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }


        /// <summary>
        /// 批量删除 根据主键
        /// </summary>
        /// <param name="idList">主键集合</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> DeleteBatchAsync(IList<long> idList)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            if (idList.IsListNullOrEmpty())
            {
                httpResponseResultModel.ErrorMessage = "id is null！";
                return httpResponseResultModel;
            }
            httpResponseResultModel.IsSuccess = await sysRoleMenuService.SoftDeleteByIdsAsync(idList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }

        /// <summary>
        /// 条件组装
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        private Expression<Func<SysRoleMenuEntity, bool>> PackageExpression(SysRoleMenuConditionDTO dto)
        {
            Expression<Func<SysRoleMenuEntity, bool>> expression = t => t.IsDeleted == false;

            //if (dto.Id.HasValue && dto.Id.Value > 0)
            //{
            //    expression = expression.And(x => x.Id == dto.Id.Value);
            //}

            return expression;
        }


        /// <summary>
        /// 导出Excel
        /// </summary>
        /// <param name="conditionDTO"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<string>> ExportToExcelAsync(SysRoleMenuConditionDTO conditionDTO)
        {
            HttpResponseResultModel<string> httpResponseResultModel = new HttpResponseResultModel<string>();
            var exportCondition = new ExportExcel<SysRoleMenuView>();
            var tags = await GetListAsync(conditionDTO);
            exportCondition.Data = tags.MapToList<SysRoleMenuView>().ToList();
            exportCondition.FileName = $"记录导出{DateTime.Now.ToString("yyMMddHHmmss")}.xlsx";
            string currentDirectory = Path.Combine(Environment.CurrentDirectory, "wwwroot", "导出");

            exportCondition.FilePath = currentDirectory + "/";
            if (!Directory.Exists(currentDirectory))
            {
                Directory.CreateDirectory(currentDirectory);
            }
            // 查询数据导出
            var res = await MyExportImportExcelHelper.ExportExcelAsync(exportCondition);
            if (res.HasError == true)
            {
                httpResponseResultModel.ErrorMessage = res.ErrorMessage;
                return httpResponseResultModel;

            }
            httpResponseResultModel.BackResult = $"/导出/{exportCondition.FileName}";
            httpResponseResultModel.IsSuccess = true;
            return httpResponseResultModel;
        }

        /// <summary>
        /// 保存数据
        /// </summary>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> SaveRole(SysRoleMenuSaveDTO dto)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>() { IsSuccess = false };
            if (dto == null)
            {
                httpResponseResultModel.ErrorMessage = "参数不能为空";
                return httpResponseResultModel;
            }

            var roleEntity = await sysRoleService.GetByIdAsync(dto.RoleId);
            if (roleEntity == null)
            {
                httpResponseResultModel.ErrorMessage = "角色信息不存在";
                return httpResponseResultModel;
            }

            var sysEntity = await sysSystemService.GetByIdAsync(dto.SystemID);

            if (sysEntity == null)
            {
                httpResponseResultModel.ErrorMessage = "系统信息不存在";
                return httpResponseResultModel;
            }

            await DeleteByRoleID(roleEntity.Id, dto.SystemID.Value);

            if (dto.MenuList.Count > 0)
            {
                var menuLs = sysMenuService.GetList(it => it.IsDeleted == false && it.SystemID == dto.SystemID);
                var buttonLs = sysMenuButtonService.GetList(it => it.IsDeleted == false);

                List<SysRoleMenuEntity> roleMenuLs = new List<SysRoleMenuEntity>();
                foreach (var item in dto.MenuList)
                {
                    if (menuLs.Exists(it => it.Id == item))
                    {
                        SysRoleMenuEntity menuEntity = new SysRoleMenuEntity();
                        menuEntity.RoleID = roleEntity.Id;
                        menuEntity.MenuID = item;
                        menuEntity.SystemID = sysEntity.Id;
                        if (buttonLs.Exists(it => it.MenuID == item))
                        {
                            var buttonCodes = buttonLs.Where(it => it.MenuID == item && dto.ButtonIdList.Contains(it.Id)).Select(it => it.Code).ToList();
                            menuEntity.MenuButtonCodes = buttonCodes == null || buttonCodes.Count <= 0 ? null : JsonConvert.SerializeObject(buttonCodes);
                        }
                        menuEntity.CreaterId = CurrentUserInfo.Instance.UserID;
                        menuEntity.ModifierId = CurrentUserInfo.Instance.UserID;
                        menuEntity.CreaterName = CurrentUserInfo.Instance.UserName;
                        menuEntity.ModifierName = CurrentUserInfo.Instance.UserName;
                        menuEntity.CreateTime = DateTime.Now;
                        menuEntity.ModifyTime = DateTime.Now;
                        roleMenuLs.Add(menuEntity);
                    }
                }
                await sysRoleMenuService.InsertRangeAsync(roleMenuLs);
            }

            httpResponseResultModel.IsSuccess = true;
            return httpResponseResultModel;

        }

        /// <summary>
        /// 逻辑删除菜单
        /// </summary>
        /// <param name="roleId"></param>
        /// <param name="systemId"></param>
        /// <returns></returns>
        public async Task DeleteByRoleID(long roleId, long systemId)
        {
            await sysRoleMenuService.GetDb().UpdateAsync(it => new SysRoleMenuEntity
            {
                IsDeleted = true,
                ModifierId = CurrentUserInfo.Instance.UserID,
                ModifierName = CurrentUserInfo.Instance.UserName,
                ModifyTime = DateTime.Now,
            }, it => it.RoleID == roleId && it.SystemID == systemId && it.IsDeleted == false);

        }


        /// <summary>
        /// 导入Excel
        /// </summary>
        public async Task<HttpResponseResultModel<string>> InsertUploadFileAsync(IFormFile file)
        {
            // 根据ID查询现有的标记，进行字段更新
            // 如果不存在，则新增标记
            HttpResponseResultModel<string> httpResponse = new HttpResponseResultModel<string>();
            var drictory = StreamToFiles(file);
            var importResult = await MyExportImportExcelHelper.ImportExcelAsync<SysRoleMenuView>(new FileStream(drictory, FileMode.Open));

            if (importResult.HasError)
            {
                httpResponse.ErrorMessage = importResult.ErrorMessage;
                return httpResponse;
            }
            var importDatas = importResult.Data;
            if (importDatas.IsListNullOrEmpty())
            {
                httpResponse.IsSuccess = false;
                httpResponse.BackResult = " 表格里没有符合条件的数据";
                return httpResponse;
            }
            var insertList = new List<SysRoleMenuEntity>();
            importDatas.ForEach(s =>
            {
                s.ID = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                insertList.Add(s.MapTo<SysRoleMenuEntity>());
            });

            var insertResult = await sysRoleMenuService.InsertRangeAsync(insertList);

            if (insertResult)
            {
                httpResponse.IsSuccess = true;
                httpResponse.BackResult = $"成功导入数据{insertList.Count}条";

            }
            else
            {
                httpResponse.ErrorMessage = "数据入库失败！";
            }
            return httpResponse;



        }
        private string StreamToFiles(IFormFile file)
        {
            try
            {
                var stream = file.OpenReadStream();
                byte[] bytes = new byte[stream.Length];
                stream.Read(bytes, 0, bytes.Length);
                stream.Seek(0, SeekOrigin.Begin);
                string path = Directory.GetCurrentDirectory() + "\\wwwroot\\" + file.FileName;
                FileStream fs = new FileStream(path, FileMode.Create);
                BinaryWriter bw = new BinaryWriter(fs);
                bw.Write(bytes);
                bw.Close();
                fs.Close();
                return path;
            }
            catch (Exception e)
            {
                throw e;
            }

        }

    }
}
