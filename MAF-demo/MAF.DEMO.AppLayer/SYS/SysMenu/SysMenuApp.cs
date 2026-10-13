//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2024/02/29 16:36:10

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
using System.Text;
using MAF.DEMO.AppLayer.SYS.SysMenu.ViewObject;
using System.Data;
using Newtonsoft.Json;
using Microsoft.IdentityModel.Tokens;
using System.Text.RegularExpressions;

namespace MAF.DEMO.AppLayer
{
    public class SysMenuApp : ISelfScopedAutoInject
    {
        private readonly ISysMenuService sysMenuService;
        private readonly ISysSystemService sysSystemService;
        private readonly ISysMenuButtonService sysMenuButtonService;
        private readonly ISysRoleService sysRoleService;
        private readonly ISysRoleMenuService sysRoleMenuService;
        private readonly ISysRoleUsersService sysRoleUsersService;

        public SysMenuApp(ISysMenuService sysMenuService
            , ISysSystemService sysSystemService
            , ISysMenuButtonService sysMenuButtonService
            , ISysRoleService sysRoleService
            , ISysRoleMenuService sysRoleMenuService
            , ISysRoleUsersService sysRoleUsersService)
        {
            this.sysMenuService = sysMenuService;
            this.sysSystemService = sysSystemService;
            this.sysMenuButtonService = sysMenuButtonService;
            this.sysRoleService = sysRoleService;
            this.sysRoleMenuService = sysRoleMenuService;
            this.sysRoleUsersService = sysRoleUsersService;
        }

        /// <summary>
        /// 根据主键获取单个实体
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        public async Task<SysMenuEntity> GetAsync(long id)
        {
            return await sysMenuService.GetByIdAsync(id).ConfigureAwait(false);
        }

        /// <summary>
        /// 根据主键获取单个实体，带菜单按钮
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        public async Task<SysMenuView> GetEntity(long id)
        {
            var menuEntity = await sysMenuService.GetByIdAsync(id).ConfigureAwait(false);
            var view = menuEntity.MapTo<SysMenuView>();
            if (!string.IsNullOrEmpty(menuEntity.NoCache))
            {
                view.NoCacheList = JsonConvert.DeserializeObject<List<string>>(menuEntity.NoCache);
            }
            view.ButtonList = sysMenuButtonService.GetList(it => it.MenuID == view.ID && it.IsDeleted == false, it => it.Sort, OrderByType.Asc);

            return view;
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
        public async Task<QueryPagedResponseModel<SysMenuEntity>> GetListPagedAsync(int page, int size, SysMenuConditionDTO conditionDTO = null, string orderByField = "Id", bool isDesc = true)
        {
            var expression = PackageExpression(conditionDTO);

            int total = 0;
            var result = sysMenuService.GetListPageOrderBy(expression, page, size, orderByField.CreateOrderByExpression<SysMenuEntity>(), isDesc ? OrderByType.Desc : OrderByType.Asc, ref total);
            return new QueryPagedResponseModel<SysMenuEntity>() { Total = total, Data = result };
        }

        /// <summary>
        /// 查询数据(不分页) 返回指定实体T
        /// </summary>
        /// <param name="conditionDTO">查询条件类</param>
        /// <param name="orderByField">排序字段</param>
        /// <param name="isDesc">Desc</param>
        /// <returns></returns>
        public async Task<List<SysMenuEntity>> GetListAsync(SysMenuConditionDTO conditionDTO, string orderByField = "Id", bool isDesc = true)
        {
            var expression = PackageExpression(conditionDTO);
            var result = sysMenuService.GetList(expression, orderByField.CreateOrderByExpression<SysMenuEntity>(), isDesc ? OrderByType.Desc : OrderByType.Asc);
            return result;
        }

        /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        public async Task<List<SysMenuEntity>> GetListByIdsAsync(List<long> ids)
        {
            if (ids.IsListNullOrEmpty())
            {
                return new List<SysMenuEntity>();
            }
            return await sysMenuService.GetListByIdsAsync(ids);
        }

        /// <summary>
        /// 获取最后一条
        /// </summary>
        /// <returns></returns>
        public async Task<SysMenuEntity> GetFirstAsync(SysMenuConditionDTO conditionDTO)
        {
            var expression = PackageExpression(conditionDTO);
            return sysMenuService.GetFirst(expression, x => x.CreateTime, OrderByType.Desc);
        }

        /// <summary>
        /// 插入单个实体
        /// </summary>
        /// <param name="insertDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<long>> InsertAsync(SysMenuInsertDTO insertDto)
        {
            HttpResponseResultModel<long> httpResponseResultModel = new HttpResponseResultModel<long>() { IsSuccess = false };
            var entity = insertDto.MapTo<SysMenuEntity>();
            if (entity.ParentID != null)
            {
                var parEntity = await this.GetAsync(entity.ParentID.Value);
                if (parEntity != null)
                {
                    entity.ParentID = parEntity.Id;
                    entity.ParentName = entity.MenuName;
                }
            }

            if (entity.ParentID != 0 && !string.IsNullOrEmpty(insertDto.RouterName)
                && sysMenuService.IsAny(it => it.IsDeleted == false && it.RouterName == insertDto.RouterName.Trim()))
            {
                httpResponseResultModel.ErrorMessage = "路由名称已存在！";
                return httpResponseResultModel;
            }
            if (!string.IsNullOrEmpty(insertDto.RouterName))
            {
                Regex regex = new Regex(@"^[a-zA-Z0-9]+$", RegexOptions.Compiled);
                if (!regex.IsMatch(insertDto.RouterName))
                {
                    httpResponseResultModel.ErrorMessage = "路由名称不能包含特殊字符，例如:【,、/、#、;】，请检查！";
                    return httpResponseResultModel;
                }
            }

            entity.RouterName = insertDto.RouterName.Trim();
            entity.MenuPath = insertDto.MenuPath.Trim();
            entity.MenuComponent = insertDto.MenuComponent.Trim();
            entity.CreaterId = CurrentUserInfo.Instance.UserID;
            entity.CreaterName = CurrentUserInfo.Instance.UserName;
            entity.CreateTime = DateTime.Now;
            entity.ModifierId = CurrentUserInfo.Instance.UserID;
            entity.ModifierName = CurrentUserInfo.Instance.UserName;
            entity.ModifyTime = DateTime.Now;
            var db = sysMenuService.GetDb().Context;
            if (insertDto.NoCacheList != null && insertDto.NoCacheList.Count > 0)
            {
                entity.NoCache = JsonConvert.SerializeObject(insertDto.NoCacheList);
            }

            db.Ado.BeginTran();
            try
            {
                var systemEntity = sysSystemService.GetById(entity.SystemID);
                if (systemEntity == null)
                {
                    httpResponseResultModel.ErrorMessage = "系统信息不存在";
                    return httpResponseResultModel;
                }

                entity.SystemID = systemEntity.Id;
                entity.SystemName = systemEntity.Name;

                if (insertDto.ButtonList != null && insertDto.ButtonList.Count > 0)
                {
                    List<SysMenuButtonEntity> buttonList = new List<SysMenuButtonEntity>();
                    foreach (var item in insertDto.ButtonList)
                    {
                        SysMenuButtonEntity buttonEntity = item.MapTo<SysMenuButtonEntity>();
                        buttonEntity.Id = buttonEntity.Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                        buttonEntity.MenuID = entity.Id;
                        buttonEntity.Sort = insertDto.ButtonList.IndexOf(item);
                        buttonEntity.CreaterId = CurrentUserInfo.Instance.UserID;
                        buttonEntity.CreaterName = CurrentUserInfo.Instance.UserName;
                        buttonEntity.CreateTime = DateTime.Now;
                        buttonEntity.ModifierId = CurrentUserInfo.Instance.UserID;
                        buttonEntity.ModifierName = CurrentUserInfo.Instance.UserName;
                        buttonEntity.ModifyTime = DateTime.Now;
                        buttonList.Add(buttonEntity);
                    }

                    await sysMenuButtonService.InsertRangeAsync(buttonList);
                }

                httpResponseResultModel.IsSuccess = await sysMenuService.InsertAsync(entity).ConfigureAwait(false);
                httpResponseResultModel.BackResult = entity.Id;
                sysMenuService.CommitTran();
            }
            catch (Exception ex)
            {
                httpResponseResultModel.IsSuccess = false;
                httpResponseResultModel.ErrorMessage = ex.Message;
                ;
                sysMenuService.RollbackTran();
            }

            return httpResponseResultModel;
        }


        /// <summary>
        /// 批量插入实体
        /// </summary>
        /// <param name="insertDtoList">对应实体数据传输对象集合</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> InsertBatchAsync(List<SysMenuInsertDTO> insertDtoList)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entityList = insertDtoList.MapToList<SysMenuEntity>();
            httpResponseResultModel.IsSuccess = await sysMenuService.InsertRangeAsync(entityList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }


        /// <summary>
        /// 根据主键更新实体
        /// </summary>
        /// <param name="id">主键</param>
        /// <param name="updateDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<long>> UpdateAsync(long id, SysMenuUpdateDTO updateDto)
        {
            HttpResponseResultModel<long> httpResponseResultModel = new HttpResponseResultModel<long>();

            var entity = updateDto.MapTo<SysMenuEntity>();

            entity.Id = id;
            if (entity.ParentID != null)
            {
                var parEntity = await this.GetAsync(entity.ParentID.Value);
                if (parEntity != null)
                {
                    entity.ParentID = parEntity.Id;
                    entity.ParentName = entity.MenuName;
                }

            }

            if (entity.ParentID != 0 && !string.IsNullOrEmpty(updateDto.RouterName)
                && sysMenuService.IsAny(it => it.IsDeleted == false && it.Id != id && it.RouterName == updateDto.RouterName.Trim()))
            {
                httpResponseResultModel.ErrorMessage = "路由名称已存在！";
                return httpResponseResultModel;
            }
            if (!string.IsNullOrEmpty(updateDto.RouterName))
            {
                Regex regex = new Regex(@"^[a-zA-Z0-9]+$", RegexOptions.Compiled);
                if (!regex.IsMatch(updateDto.RouterName))
                {
                    httpResponseResultModel.ErrorMessage = "路由名称不能包含特殊字符，例如:【,、/、#、;】，请检查！";
                    return httpResponseResultModel;
                }
            }

            entity.RouterName = updateDto.RouterName.Trim();
            entity.MenuPath = updateDto.MenuPath.Trim();
            entity.MenuComponent = updateDto.MenuComponent.Trim();
            entity.ModifierId = CurrentUserInfo.Instance.UserID;
            entity.ModifierName = CurrentUserInfo.Instance.UserName;
            entity.ModifyTime = DateTime.Now;
            if (updateDto.NoCacheList != null && updateDto.NoCacheList.Count > 0)
            {
                entity.NoCache = JsonConvert.SerializeObject(updateDto.NoCacheList);
            }


            var systemEntity = sysSystemService.GetById(entity.SystemID);
            if (systemEntity == null)
            {
                httpResponseResultModel.ErrorMessage = "系统信息不存在";
                return httpResponseResultModel;
            }

            var db = sysMenuService.GetDb().Context;
            db.Ado.BeginTran();
            try
            {
                List<SysMenuButtonEntity> oldButtonList = await sysMenuButtonService.GetListAsync(it => it.MenuID == entity.Id && it.IsDeleted == false);

                List<dynamic> delIds = new List<dynamic>();

                List<SysMenuButtonEntity> updateLs = new List<SysMenuButtonEntity>();
                List<SysMenuButtonEntity> insertLs = new List<SysMenuButtonEntity>();

                foreach (var item in oldButtonList)
                {
                    if (!updateDto.ButtonList.Exists(it => it.Code == item.Code))
                    {
                        delIds.Add(item.Id);
                    }
                }

                foreach (var item in updateDto.ButtonList)
                {
                    SysMenuButtonEntity buttonEntity = item.MapTo<SysMenuButtonEntity>();
                    if (oldButtonList.Exists(it => it.Code == item.Code))
                    {
                        buttonEntity = oldButtonList.FirstOrDefault(it => it.Code == item.Code);
                        buttonEntity.Name = item.Name;
                        buttonEntity.Sort = updateDto.ButtonList.IndexOf(item);
                        buttonEntity.ModifierId = CurrentUserInfo.Instance.UserID;
                        buttonEntity.ModifierName = CurrentUserInfo.Instance.UserName;
                        buttonEntity.ModifyTime = DateTime.Now;
                        updateLs.Add(buttonEntity);
                    }
                    else
                    {
                        buttonEntity = item.MapTo<SysMenuButtonEntity>();
                        buttonEntity.Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                        buttonEntity.Sort = updateDto.ButtonList.IndexOf(item);
                        buttonEntity.MenuID = entity.Id;
                        buttonEntity.CreaterId = CurrentUserInfo.Instance.UserID;
                        buttonEntity.CreaterName = CurrentUserInfo.Instance.UserName;
                        buttonEntity.CreateTime = DateTime.Now;
                        buttonEntity.ModifierId = CurrentUserInfo.Instance.UserID;
                        buttonEntity.ModifierName = CurrentUserInfo.Instance.UserName;
                        buttonEntity.ModifyTime = DateTime.Now;
                        insertLs.Add(buttonEntity);
                    }
                }

                if (updateLs.Count > 0)
                {
                    await db.Updateable(updateLs).ExecuteCommandAsync();
                }

                if (insertLs.Count > 0)
                {
                    await db.Insertable(insertLs).ExecuteCommandAsync();
                }

                if (delIds.Count > 0)
                {
                    await db.Updateable<SysMenuButtonEntity>().Where(it => delIds.Contains(it.Id)).SetColumns(it => new SysMenuButtonEntity { IsDeleted = true }).ExecuteCommandAsync();
                }

                entity.SystemID = systemEntity.Id;
                entity.SystemName = systemEntity.Name;
                httpResponseResultModel.IsSuccess = await db.Updateable(entity).IgnoreColumns(ignoreAllNullColumns: true).ExecuteCommandAsync() > 0;
                httpResponseResultModel.BackResult = entity.Id;
                db.Ado.CommitTran();
            }
            catch (Exception ex)
            {
                httpResponseResultModel.IsSuccess = false;
                httpResponseResultModel.ErrorMessage = ex.Message;
                ;
                sysMenuService.RollbackTran();
            }

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
            httpResponseResultModel.IsSuccess = await sysMenuService.SoftDeleteByIdAsync(id);
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
            httpResponseResultModel.IsSuccess = await sysMenuService.SoftDeleteByIdsAsync(idList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }

        /// <summary>
        /// 条件组装
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        private Expression<Func<SysMenuEntity, bool>> PackageExpression(SysMenuConditionDTO dto)
        {
            Expression<Func<SysMenuEntity, bool>> expression = t => t.IsDeleted == false;

            //if (dto.Id.HasValue && dto.Id.Value > 0)
            //{
            //    expression = expression.And(x => x.Id == dto.Id.Value);
            //}
            if (!string.IsNullOrEmpty(dto.MenuName))
            {
                expression = expression.And(x => x.MenuName.Contains(dto.MenuName));
            }

            if (dto.SystemID.HasValue)
            {
                if (dto.SystemID > 0)
                {
                    expression = expression.And(x => x.SystemID == dto.SystemID.Value);
                }
            }

            if (!string.IsNullOrEmpty(dto.SystemName))
            {
                expression = expression.And(x => x.SystemName.Contains(dto.SystemName));
            }

            if (dto.ParentID.HasValue)
            {
                if (dto.ParentID > 0)
                {
                    expression = expression.And(x => x.ParentID == dto.ParentID.Value);
                }
            }

            if (!string.IsNullOrEmpty(dto.ParentName))
            {
                expression = expression.And(x => x.ParentName.Contains(dto.ParentName));
            }

            if (dto.Status.HasValue)
            {
                if (dto.Status > 0)
                {
                    expression = expression.And(x => x.Status == dto.Status.Value);
                }
            }

            return expression;
        }

        /// <summary>
        /// 查找所有下级数据
        /// </summary>
        /// <returns></returns>
        public async Task<List<SysMenuTreeEntity>> GetTreeChildList()
        {
            var ls = await sysMenuService.GetTreeChildList(0);
            List<SysMenuTreeEntity> list = new List<SysMenuTreeEntity>();
            list.Add(new SysMenuTreeEntity { MenuName = "根目录", Id = 0, ParentID = 0, Children = ls });
            return list;
        }


        /// <summary>
        /// 查询菜单树数据
        /// </summary> 
        /// <param name="conditionDTO">查询条件类</param> sc</param>
        /// <returns></returns>
        public async Task<List<SysMenuTreeEntity>> GetTreetList(SysMenuConditionDTO conditionDTO)
        {
            var expression = PackageExpression(conditionDTO);
            var ls = await sysMenuService.GetListAsync(expression, OrderByType.Asc, x => x.Sort, x => new SysMenuTreeEntity());
            return new TreeBuildUtil<SysMenuTreeEntity>().DoTreeBuild(ls);
        }

        /// <summary>
        /// 导出Excel
        /// </summary>
        /// <param name="conditionDTO"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<string>> ExportToExcelAsync(SysMenuConditionDTO conditionDTO)
        {
            HttpResponseResultModel<string> httpResponseResultModel = new HttpResponseResultModel<string>();
            var exportCondition = new ExportExcel<SysMenuView>();
            var tags = await GetListAsync(conditionDTO);
            exportCondition.Data = tags.MapToList<SysMenuView>().ToList();
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
            var importResult = await MyExportImportExcelHelper.ImportExcelAsync<SysMenuView>(new FileStream(drictory, FileMode.Open));

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

            var insertList = new List<SysMenuEntity>();
            importDatas.ForEach(s =>
            {
                s.ID = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                insertList.Add(s.MapTo<SysMenuEntity>());
            });

            var insertResult = await sysMenuService.InsertRangeAsync(insertList);

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
        /// 获取顺序
        /// </summary>
        /// <param name="menuId"></param>
        /// <param name="systemId"></param>
        /// <returns></returns>
        public async Task<int> GetSort(long menuId, long systemId)
        {
            return sysMenuService.Max(it => it.ParentID == menuId && it.IsDeleted == false && it.SystemID == systemId, it => it.Sort.Value) + 1;
        }


        /// <summary>
        /// 根据用户获取路由
        /// </summary>
        /// <returns></returns>
        public async Task<List<RouteMenuViewModel>> GetRouteList()
        {
            long userId = CurrentUserInfo.Instance.UserID;

            List<SysMenuEntity> menuList = new List<SysMenuEntity>();

            if (CurrentUserInfo.Instance.AccountType == AccountTypeEnum.SuperAdministrator)
            {
                menuList = await GetListAsync(new SysMenuConditionDTO() { SystemID = CurrentUserInfo.Instance.SystemID }, "Sort", true);
            }
            else
            {
                var roleIds = await sysRoleUsersService.GetDb().AsQueryable()
                   .InnerJoin<SysRoleEntity>((u, r) => u.RoleID == r.Id && u.IsDeleted == false && r.IsDeleted == false && r.Status == DataDictionary.Code.Status.正常)
                   .Where(u => u.UserID == userId)
                   .Select(u => u.RoleID).ToListAsync();

                menuList = await sysRoleMenuService.GetDb().AsQueryable().InnerJoin<SysMenuEntity>((r, m) => r.MenuID == m.Id)
                    .Where((r, m) => roleIds.Contains(r.RoleID.Value) && r.SystemID == CurrentUserInfo.Instance.SystemID &&
                    r.IsDeleted == false && m.IsDeleted == false && m.Status == DataDictionary.Code.Status.正常)
                    .Select((r, m) => m).Distinct().ToListAsync();
            }

            List<RouteMenuViewModel> ls = await GetMenuTree(menuList, new SysMenuEntity { ParentID = 0, Id = 0 });
            return ls;
        }

        /// <summary>
        /// 递归菜单
        /// </summary>
        /// <param name="lls"></param>
        /// <param name="menuViewModel"></param>
        /// <returns></returns>
        public async Task<List<RouteMenuViewModel>> GetMenuTree(List<SysMenuEntity> lls, SysMenuEntity menuViewModel)
        {
            List<RouteMenuViewModel> node = new List<RouteMenuViewModel>();
            List<SysMenuEntity> menuTree = lls.Where(it => it.ParentID == menuViewModel.Id).OrderBy(it => it.Sort).ToList();
            foreach (var item in menuTree)
            {
                RouteMenuViewModel routeMenu = new RouteMenuViewModel();
                routeMenu.Meta = new MetaViewModel();
                routeMenu.Meta.Title = string.IsNullOrEmpty(item.MenuI18n) || item.MenuI18n.ToLower() == "null" ? item.MenuName : item.MenuI18n;
                routeMenu.Meta.Icon = item.MenuIcon;
                routeMenu.Meta.ActiveMenu = item.MenuActive;
                routeMenu.Meta.ActiveIcon = item.MenuActiveIcon;
                routeMenu.Meta.Link = item.Link;
                routeMenu.Meta.SingleMenu = item.MenuSingle.Value;
                routeMenu.Meta.Menu = item.MenuDisplay.Value;
                routeMenu.Meta.Cache = item.EnableCache.Value;
                routeMenu.Meta.NewWindow = item.MenuNewWindow.Value;
                routeMenu.Meta.Maximize = item.MenuMaximize.Value;
                routeMenu.Meta.Badge = item.MenuBadge.Value;
                routeMenu.Meta.DefaultOpened = item.MenuDefaultOpened.Value;
                routeMenu.Meta.AlwaysOpened = item.MenuAlwaysOpened.Value;
                routeMenu.Meta.Permanent = item.MenuPermanent.Value;
                routeMenu.Meta.Iframe = item.MenuIframe;
                routeMenu.Meta.Copyright = item.MenuCopyright.Value;
                routeMenu.Meta.WhiteList = item.IsWhitelist.Value;
                routeMenu.Meta.Breadcrumb = item.MenuBreadcrumb.Value;
                routeMenu.Path = item.MenuSingle == true ? (item.MenuPath.Substring(0, 1) == "/" ? item.MenuPath : "/" + item.MenuPath) : item.MenuPath;
                routeMenu.Component = item.MenuComponent;
                routeMenu.Component = string.IsNullOrWhiteSpace(item.MenuComponent) ? "Layout" : item.MenuComponent;

                if (!string.IsNullOrEmpty(item.NoCache))
                {
                    routeMenu.Meta.NoCache = string.IsNullOrEmpty(item.NoCache) ? null : JsonConvert.DeserializeObject<List<string>>(item.NoCache);
                }

                if (routeMenu.Component == "Layout" && !string.IsNullOrWhiteSpace(item.MenuPath))
                {
                    routeMenu.Path = item.MenuPath.Substring(0, 1) == "/" ? item.MenuPath : "/" + item.MenuPath;
                }
                if (routeMenu.Component != "Layout")
                {
                    routeMenu.Component = item.MenuComponent.Substring(0, 1) == "/" ? item.MenuComponent.Substring(1) : item.MenuComponent;
                }
                routeMenu.Name = item.RouterName;
                routeMenu.Redirect = string.IsNullOrEmpty(item.MenuRedirect) ? null : item.MenuRedirect;
                routeMenu.Children = await GetMenuTree(lls, item);
                node.Add(routeMenu);
            }

            return node;
        }

        /// <summary>
        /// 根据角色ID获取菜单树
        /// </summary>
        /// <param name="roleId"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<List<MenuTreeViewModel>>> GetTreeMenuListByRoleId(SysRoleMenuConditionDTO dto)
        {
            HttpResponseResultModel<List<MenuTreeViewModel>> httpRequestModel = new HttpResponseResultModel<List<MenuTreeViewModel>> { IsSuccess = false };
            var roleEntity = sysRoleService.GetById(dto.RoleID);
            if (roleEntity == null)
            {
                httpRequestModel.ErrorMessage = "角色信息不存在";
                return httpRequestModel;
            }

            var roleLs = await sysRoleMenuService.GetListAsync(it => it.RoleID == roleEntity.Id && it.SystemID == dto.SystemID && it.IsDeleted == false);
            var menuLs = sysMenuService.GetList(it => it.IsDeleted == false && dto.SystemID == dto.SystemID);
            var buttonLs = sysMenuButtonService.GetList(it => it.IsDeleted == false);
            var list = await GetMenuButtonTreeChild(menuLs, new MenuTreeViewModel { ID = 0 }, roleLs, buttonLs);

            httpRequestModel.BackResult = list;
            httpRequestModel.IsSuccess = true;
            return httpRequestModel;
        }

        /// <summary>
        /// 递归菜单树
        /// </summary>
        /// <param name="lls"></param>
        /// <param name="conditionInput"></param>
        /// <param name="roleList"></param>
        /// <param name="buttonList"></param>
        /// <returns></returns>
        public async Task<List<MenuTreeViewModel>> GetMenuButtonTreeChild(List<SysMenuEntity> lls, MenuTreeViewModel conditionInput, List<SysRoleMenuEntity> roleList,
            List<SysMenuButtonEntity> buttonList)
        {
            List<SysMenuEntity> ls = lls.Where(it => it.ParentID == conditionInput.ID).ToList();
            List<MenuTreeViewModel> node = new List<MenuTreeViewModel>();
            foreach (var item in ls)
            {
                MenuTreeViewModel model = new MenuTreeViewModel();
                model.ID = item.Id;
                model.ParentID = item.ParentID;
                model.MenuName = item.MenuName;
                model.Children = await GetMenuButtonTreeChild(lls, model, roleList, buttonList);
                model.IsChecked = roleList.Exists(it => it.MenuID == item.Id);

                SysRoleMenuEntity roleMenuEntity = roleList.FirstOrDefault(it => it.MenuID == item.Id);
                model.IsChecked = roleMenuEntity != null;
                model.MenuButtonList = new List<ButtonView>();
                foreach (var button in buttonList.Where(it => it.MenuID == item.Id))
                {
                    ButtonView buttonView = new ButtonView();
                    buttonView.ID = button.Id;
                    buttonView.MenuID = button.MenuID;
                    buttonView.Code = button.Code;
                    buttonView.Name = button.Name;
                    buttonView.IsChecked = roleMenuEntity != null ? roleMenuEntity.MenuButtonCodes.Contains(button.Code) : false;
                    model.MenuButtonList.Add(buttonView);
                }

                node.Add(model);
            }

            return node;
        }


        /// <summary>
        /// 根据角色获取菜单
        /// </summary>
        /// <param name="conditionDTO">查询条件类</param> 
        /// <returns></returns>
        public async Task<HttpResponseResultModel<dynamic>> GetMenuListByRole(SysRoleMenuConditionDTO conditionDTO)
        {

            HttpResponseResultModel<dynamic> httpResponseResultModel = new HttpResponseResultModel<dynamic>();
            var roleEntity = await sysRoleService.GetByIdAsync(conditionDTO.RoleID);
            if (roleEntity == null)
            {
                httpResponseResultModel.ErrorMessage = "角色信息不存在";
                return httpResponseResultModel;
            }

            var menuLs = sysMenuService.GetList(it => it.IsDeleted == false && it.SystemID == conditionDTO.SystemID, it => it.Sort, OrderByType.Asc);
            var roleMenuLs = await sysRoleMenuService.GetListAsync(it => it.RoleID == roleEntity.Id && it.SystemID == conditionDTO.SystemID && it.IsDeleted == false);
            var buttonLs = sysMenuButtonService.GetList(it => it.IsDeleted == false);

            List<MenuTreeViewModel> list = new List<MenuTreeViewModel>();
            foreach (var item in menuLs)
            {
                SysRoleMenuEntity roleMenuEntity = roleMenuLs.FirstOrDefault(it => it.MenuID == item.Id);

                MenuTreeViewModel menuTreeEntity = item.MapTo<MenuTreeViewModel>();
                menuTreeEntity.MenuButtonList = new List<ButtonView>();
                menuTreeEntity.IsChecked = roleMenuEntity != null;
                menuTreeEntity.ButtonIdList = new List<long>();
                List<string> roleMenuButtonList = new List<string>();
                if (roleMenuEntity != null && !string.IsNullOrWhiteSpace(roleMenuEntity.MenuButtonCodes))
                {
                    roleMenuButtonList = JsonConvert.DeserializeObject<List<string>>(roleMenuEntity.MenuButtonCodes);
                }

                foreach (var button in buttonLs.Where(it => it.MenuID == item.Id).OrderBy(it => it.Sort).ToList())
                {
                    ButtonView buttonView = new ButtonView();
                    buttonView.ID = button.Id;
                    buttonView.MenuID = button.MenuID;
                    buttonView.Code = button.Code;
                    buttonView.Name = button.Name;
                    buttonView.IsChecked = false;
                    if (roleMenuButtonList != null && roleMenuButtonList.Count > 0)
                    {
                        buttonView.IsChecked = roleMenuButtonList.Exists(it => it == button.Code);

                    }
                    if (buttonView.IsChecked)
                    {
                        menuTreeEntity.ButtonIdList.Add(buttonView.ID);
                    }
                    menuTreeEntity.MenuButtonList.Add(buttonView);
                }

                list.Add(menuTreeEntity);
            }

            httpResponseResultModel.IsSuccess = true;
            httpResponseResultModel.BackResult = new { DataList = list };
            return httpResponseResultModel;
        }
    }
}