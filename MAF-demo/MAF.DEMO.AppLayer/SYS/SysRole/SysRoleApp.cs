
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 09:39:57
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System.Linq.Expressions;
using System;
using MAF.DEMO.IDbService;
using SqlSugar;
using MAF.DEMO.Utility;
using MAF.DEMO.Core;
using MAF.DEMO.AppLayer;
using MAF.DEMO.AppLayer.SYS.ViewObject;
using MAF.DEMO.AppLayer.SYS.DTOS;
using MAF.DEMO.NPOIHelper;
using System.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MAF.DEMO.AppLayer.Basic.DTOS;
using MAF.DEMO.AppLayer.SYS.SysUser.DTOS;
using MAF.DEMO.IDbService.IService.System;
using MAF.DEMO.IDbService.Models.System;
using Microsoft.IdentityModel.Tokens;
namespace MAF.DEMO.AppLayer
{
    public class SysRoleApp : ISelfScopedAutoInject
    {
        private readonly ISysRoleService sysRoleService;
        private readonly ISysRoleUsersService sysRoleUsersService;
        private readonly ISysUserService sysUserService;


        public SysRoleApp(ISysRoleService sysRoleService, ISysRoleUsersService sysRoleUsersService, ISysUserService sysUserService)
        {
            this.sysRoleService = sysRoleService;
            this.sysRoleUsersService = sysRoleUsersService;
            this.sysUserService = sysUserService;
        }

        /// <summary>
        /// 根据主键获取单个实体
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        public async Task<SysRoleEntity> GetAsync(long id)
        {
            return await sysRoleService.GetByIdAsync(id).ConfigureAwait(false);
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
        public async Task<QueryPagedResponseModel<SysRoleEntity>> GetListPagedAsync(int page, int size, SysRoleConditionDTO conditionDTO = null, string orderByField = "Id", bool isDesc = true)
        {
            var expression = PackageExpression(conditionDTO);

            int total = 0;
            var result = sysRoleService.GetListPageOrderBy(expression, page, size, orderByField.CreateOrderByExpression<SysRoleEntity>(), isDesc ? OrderByType.Desc : OrderByType.Asc, ref total);
            return new QueryPagedResponseModel<SysRoleEntity>()
            {
                Total = total,
                Data = result
            };
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
        public async Task<QueryPagedResponseModel<SysUserEntity>> GetUserListPageByRole(int page, int size, SysRoleConditionDTO conditionDTO = null, string orderByField = "Id", bool isDesc = true)
        {
            int total = 0;
            var result = sysRoleUsersService.GetDb().AsQueryable().InnerJoin<SysUserEntity>((r, u) => r.UserID == u.Id)
                .Where((r, u) => r.IsDeleted == false && r.RoleID == conditionDTO.RoleId)
                .WhereIF(!string.IsNullOrEmpty(conditionDTO.Name), (r, u) => u.Name.Contains(conditionDTO.Name.Trim()))
                .OrderBy((r, u) => u.Name)
                .Select((r, u) => u)
                .ToPageList(page, size, ref total);
            return new QueryPagedResponseModel<SysUserEntity>()
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
        public async Task<List<SysRoleEntity>> GetListAsync(SysRoleConditionDTO conditionDTO, string orderByField = "Id", bool isDesc = true)
        {
            var expression = PackageExpression(conditionDTO);
            var result = sysRoleService.GetList(expression, orderByField.CreateOrderByExpression<SysRoleEntity>(), isDesc ? OrderByType.Desc : OrderByType.Asc);
            return result;
        }

        /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        public async Task<List<SysRoleEntity>> GetListByIdsAsync(List<long> ids)
        {
            if (ids.IsListNullOrEmpty())
            {
                return new List<SysRoleEntity>();
            }
            return await sysRoleService.GetListByIdsAsync(ids);
        }

        /// <summary>
        /// 获取最后一条
        /// </summary>
        /// <returns></returns>
        public async Task<SysRoleEntity> GetFirstAsync(SysRoleConditionDTO conditionDTO)
        {
            var expression = PackageExpression(conditionDTO);
            return sysRoleService.GetFirst(expression, x => x.CreateTime, OrderByType.Desc);
        }

        /// <summary>
        /// 插入单个实体
        /// </summary>
        /// <param name="insertDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<long>> InsertAsync(SysRoleInsertDTO insertDto)
        {
            HttpResponseResultModel<long> httpResponseResultModel = new HttpResponseResultModel<long>();
            var entity = insertDto.MapTo<SysRoleEntity>();

            Expression<Func<SysRoleEntity, bool>> expression = t => t.IsDeleted == false;
            if (!string.IsNullOrEmpty(insertDto.Name))
            {
                expression = expression.And(x => x.Name == insertDto.Name);
            }
            var result = sysRoleService.GetList(expression, "Id".CreateOrderByExpression<SysRoleEntity>(), true ? OrderByType.Desc : OrderByType.Asc);
            if (result.Count > 0)
            {
                httpResponseResultModel.IsSuccess = false;
                httpResponseResultModel.ErrorMessage = "角色已存在";
                return httpResponseResultModel;
            }

            httpResponseResultModel.IsSuccess = await sysRoleService.InsertAsync(entity).ConfigureAwait(false);
            httpResponseResultModel.BackResult = entity.Id;
            return httpResponseResultModel;
        }

        /// <summary>
        /// 批量插入实体
        /// </summary>
        /// <param name="insertDtoList">对应实体数据传输对象集合</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> InsertBatchAsync(List<SysRoleInsertDTO> insertDtoList)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entityList = insertDtoList.MapToList<SysRoleEntity>();
            httpResponseResultModel.IsSuccess = await sysRoleService.InsertRangeAsync(entityList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }


        /// <summary>
        /// 根据主键更新实体
        /// </summary>
        /// <param name="id">主键</param>
        /// <param name="updateDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> UpdateAsync(long id, SysRoleUpdateDTO updateDto)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entity = updateDto.MapTo<SysRoleEntity>();
            entity.Id = id;
            httpResponseResultModel.IsSuccess = await sysRoleService.UpdateAsync(entity);
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
            httpResponseResultModel.IsSuccess = await sysRoleService.SoftDeleteByIdAsync(id);
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
            httpResponseResultModel.IsSuccess = await sysRoleService.SoftDeleteByIdsAsync(idList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }

        /// <summary>
        /// 条件组装
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        private Expression<Func<SysRoleEntity, bool>> PackageExpression(SysRoleConditionDTO dto)
        {
            Expression<Func<SysRoleEntity, bool>> expression = t => t.IsDeleted == false;

            //if (dto.Id.HasValue && dto.Id.Value > 0)
            //{
            //    expression = expression.And(x => x.Id == dto.Id.Value);
            //}

            if (!string.IsNullOrEmpty(dto.Name))
            {
                expression = expression.And(x => x.Name.Contains(dto.Name));
            }

            return expression;
        }


        /// <summary>
        /// 导出Excel
        /// </summary>
        /// <param name="conditionDTO"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<string>> ExportToExcelAsync(SysRoleConditionDTO conditionDTO)
        {
            HttpResponseResultModel<string> httpResponseResultModel = new HttpResponseResultModel<string>();
            var exportCondition = new ExportExcel<SysRoleView>();
            var tags = await GetListAsync(conditionDTO);
            exportCondition.Data = tags.MapToList<SysRoleView>().ToList();
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
        /// 导入Excel
        /// </summary>
        public async Task<HttpResponseResultModel<string>> InsertUploadFileAsync(IFormFile file)
        {
            // 根据ID查询现有的标记，进行字段更新
            // 如果不存在，则新增标记
            HttpResponseResultModel<string> httpResponse = new HttpResponseResultModel<string>();
            var drictory = StreamToFiles(file);
            var importResult = await MyExportImportExcelHelper.ImportExcelAsync<SysRoleView>(new FileStream(drictory, FileMode.Open));

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
            var insertList = new List<SysRoleEntity>();
            importDatas.ForEach(s =>
            {
                s.ID = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                insertList.Add(s.MapTo<SysRoleEntity>());
            });

            var insertResult = await sysRoleService.InsertRangeAsync(insertList);

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

        /// <summary>
        /// 修改状态
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> UpdateStatus(SysRoleUpdateDTO dto)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var userEntity = sysRoleService.GetById(dto.ID);
            if (userEntity == null)
            {
                httpResponseResultModel.ErrorMessage = "数据不存在";
                return httpResponseResultModel;
            }

            userEntity.Status = dto.Status;
            httpResponseResultModel.IsSuccess = await sysRoleService.UpdateAsync(userEntity);
            return httpResponseResultModel;
        }

        /// <summary>
        /// 保存用户角色关联
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> SaveUser(PermissionAllocationDTO dto)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            if (dto.RoleIds == null || dto.RoleIds.Count <= 0)
            {
                httpResponseResultModel.ErrorMessage = "角色不能为空";
                return httpResponseResultModel;
            }
            if (dto.UserIds == null || dto.UserIds.Count <= 0)
            {
                httpResponseResultModel.ErrorMessage = "用户不能为空";
                return httpResponseResultModel;
            }

            var roleUserList = await sysRoleUsersService.GetListAsync(it => it.IsDeleted == false && it.RoleID == dto.RoleIds[0]);
            List<SysRoleUsersEntity> saveList = new List<SysRoleUsersEntity>();
            foreach (var item in dto.UserIds)
            {
                if (roleUserList != null && roleUserList.Any(it => it.UserID == item))
                {
                    continue;
                }

                if (saveList.Any(it => it.UserID == item))
                {
                    continue;
                }
                SysRoleUsersEntity sysRoleUsersEntity = new SysRoleUsersEntity();
                sysRoleUsersEntity.Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                sysRoleUsersEntity.RoleID = dto.RoleIds[0];
                sysRoleUsersEntity.UserID = item;
                sysRoleUsersEntity.CreaterId = CurrentUserInfo.Instance.UserID;
                sysRoleUsersEntity.CreaterName = CurrentUserInfo.Instance.UserName;
                sysRoleUsersEntity.CreateTime = DateTime.Now;
                sysRoleUsersEntity.ModifierId = CurrentUserInfo.Instance.UserID;
                sysRoleUsersEntity.ModifierName = CurrentUserInfo.Instance.UserName;
                sysRoleUsersEntity.ModifyTime = DateTime.Now;
                saveList.Add(sysRoleUsersEntity);
            }
            if (saveList.Count > 0)
            {
                await sysRoleUsersService.BulkCopyAsync(saveList);
            }

            httpResponseResultModel.IsSuccess = true;
            return httpResponseResultModel;

        }


        /// <summary>
        /// 保存用户角色关联
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> DeleteUser(PermissionAllocationDTO dto)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            if (dto.RoleIds == null || dto.RoleIds.Count <= 0)
            {
                httpResponseResultModel.ErrorMessage = "角色不能为空";
                return httpResponseResultModel;
            }
            if (dto.UserIds == null || dto.UserIds.Count <= 0)
            {
                httpResponseResultModel.ErrorMessage = "用户不能为空";
                return httpResponseResultModel;
            }

            await sysRoleUsersService.DeleteAsync(it => dto.UserIds.Contains(it.UserID.Value) && dto.RoleIds.Contains(it.RoleID.Value));
            httpResponseResultModel.IsSuccess = true;
            return httpResponseResultModel;
        }
    }
}
