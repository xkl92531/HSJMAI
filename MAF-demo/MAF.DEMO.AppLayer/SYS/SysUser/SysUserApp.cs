
using MAF.DEMO.AppLayer.SYS.SysUser.DTOS;
using MAF.DEMO.Core;
using MAF.DEMO.IDbService;
using MAF.DEMO.IDbService.IService.System;
using MAF.DEMO.IDbService.Models.System;
using MAF.DEMO.Utility;
using Newtonsoft.Json;
using SqlSugar;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;
using MAF.DEMO.AppLayer.SYS.DTOS;

namespace MAF.DEMO.AppLayer.SYS.SysUser
{
    /// <summary>
    /// 用户账号App
    /// </summary>
    public class SysUserApp : ISelfScopedAutoInject
    {
        private readonly ISysUserService userService;
        private readonly ISysRoleUsersService roleUsersService;
        private readonly ISysRoleMenuService roleMenuService;
        private readonly ISysRoleService roleService;
        private readonly ISysMenuService menuService;
        private readonly ISysMenuButtonService menuButtonService;
        public SysUserApp(ISysUserService userService, ISysRoleUsersService roleUsersService, ISysRoleMenuService roleMenuService, ISysRoleService roleService, ISysMenuService menuService, ISysMenuButtonService menuButtonService)
        {
            this.userService = userService;
            this.roleUsersService = roleUsersService;
            this.roleMenuService = roleMenuService;
            this.roleService = roleService;
            this.menuService = menuService;
            this.menuButtonService = menuButtonService;
        }

        /// <summary>
        /// 根据主键获取单个实体
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        public async Task<SysUserEntity> GetAsync(long id)
        {
            return await userService.GetByIdAsync(id).ConfigureAwait(false);
        }


        /// <summary>
        /// 根据主键获取单个实体
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        public async Task<SysUserView> GetEntity(long id)
        {

            var entity = await userService.GetByIdAsync(id).ConfigureAwait(false);
            if (entity != null)
            {
                var view = entity.MapTo<SysUserView>();
                view.UserRoleList = roleUsersService.GetDb().AsQueryable().Where(it => it.IsDeleted == false && it.UserID == entity.Id).Select(it => it.RoleID.Value).Distinct().ToList();
                view.Password = "";
                return view;
            }
            return null;
        }


        /// <summary>
        /// 查询数据(分页) 返回指定实体T
        /// </summary>
        /// <param name="page">页码</param>
        /// <param name="size">每页数量</param>
        /// <param name="conditionDTO">查询条件类</param>
        /// <param name="orderByField">排序字段</param>
        /// <param name="isDesc">是否倒序</param>
        /// <returns></returns>
        public async Task<QueryPagedResponseModel<SysUserEntity>> GetListPagedAsync(int page, int size, SysUserConditionDTO conditionDTO = null, string orderByField = "Id", bool isDesc = true)
        {
            var expression = PackageExpression(conditionDTO);

            int total = 0;
            var result = userService.GetListPageOrderBy(expression, page, size, orderByField.CreateOrderByExpression<SysUserEntity>(), isDesc ? OrderByType.Desc : OrderByType.Asc, ref total);
            return new QueryPagedResponseModel<SysUserEntity>()
            {
                Total = total,
                Data = result
            };
        }

        /// <summary>
        /// 查询不分页
        /// </summary>
        /// <param name="conditionDTO"></param>
        /// <param name="orderByField"></param>
        /// <param name="isDesc"></param>
        /// <returns></returns>
        public async Task<List<SysUserEntity>> GetListAsync(SysUserConditionDTO conditionDTO, string orderByField = "Id", bool isDesc = true)
        {
            var expression = PackageExpression(conditionDTO);

            var result = userService.GetList(expression, orderByField.CreateOrderByExpression<SysUserEntity>(), isDesc ? OrderByType.Desc : OrderByType.Asc);
            return result;
        }

        /// <summary>
        /// 获取最后一条
        /// </summary>
        /// <returns></returns>
        public async Task<SysUserEntity> GetFirstAsync(SysUserConditionDTO conditionDTO)
        {
            var expression = PackageExpression(conditionDTO);
            return userService.GetFirst(expression, x => x.CreateTime, OrderByType.Desc);
        }

        /// <summary>
        /// 根据id集合获取多条数据
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        public async Task<List<SysUserEntity>> GetListByIdsAsync(List<long> ids)
        {
            if (ids.IsListNullOrEmpty())
            {
                return new List<SysUserEntity>();
            }
            return await userService.GetListByIdsAsync(ids);
        }

        /// <summary>
        /// 插入单个实体
        /// </summary>
        /// <param name="insertDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<long>> InsertAsync(SysUserInsertDTO insertDto)
        {
            HttpResponseResultModel<long> httpResponseResultModel = new HttpResponseResultModel<long>();
            if (string.IsNullOrEmpty(insertDto.Account))
            {
                httpResponseResultModel.ErrorMessage = "账号不能为空！";
                return httpResponseResultModel;
            }
            if (userService.IsAny(it => it.IsDeleted == false && it.Account == insertDto.Account))
            {
                httpResponseResultModel.ErrorMessage = "账号已存在！";
                return httpResponseResultModel;
            }
            if (string.IsNullOrEmpty(insertDto.Name))
            {
                httpResponseResultModel.ErrorMessage = "姓名不能为空";
                return httpResponseResultModel;
            }
            var userEntity = insertDto.MapTo<SysUserEntity>();
            string pwd = EncryptDecryptHelper.Md5(AppsettingsConfig.DefaultPwd);

            if (!string.IsNullOrEmpty(insertDto.Password))
            {
                pwd = EncryptDecryptHelper.Md5(insertDto.Password.Trim());
            }

            userEntity.Account = insertDto.Account.Trim();
            userEntity.Password=  pwd;
            httpResponseResultModel.IsSuccess = await userService.InsertAsync(userEntity);

            await roleUsersService.DeleteAsync(it => it.IsDeleted == false && it.UserID == userEntity.Id);

            if (insertDto.UserRoleList.Count > 0)
            {
                List<SysRoleUsersEntity> userRoleList = new List<SysRoleUsersEntity>();
                foreach (var item in insertDto.UserRoleList)
                {
                    if (!userRoleList.Exists(it => it.RoleID == item))
                    {
                        SysRoleUsersEntity entity = new SysRoleUsersEntity();
                        entity.Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                        entity.UserID = userEntity.Id;
                        entity.RoleID = item;
                        userRoleList.Add(entity);
                    }
                }

                if (userRoleList.Count > 0)
                {
                    await roleUsersService.InsertRangeAsync(userRoleList);
                }
            }


            httpResponseResultModel.BackResult = userEntity.Id;
            return httpResponseResultModel;
        }

        /// <summary>
        /// 批量插入实体
        /// </summary>
        /// <param name="insertDtoList">对应实体数据传输对象集合</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> InsertBatchAsync(List<SysUserInsertDTO> insertDtoList)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var entitys = insertDtoList.MapToList<SysUserEntity>();
            httpResponseResultModel.IsSuccess = await userService.InsertRangeAsync(entitys);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }


        /// <summary>
        /// 根据主键更新实体
        /// </summary>
        /// <param name="id">主键</param>
        /// <param name="updateDto">对应实体数据传输对象</param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> UpdateAsync(long id, SysUserUpdateDTO updateDto)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();

          var userEntity= await  userService.GetByIdAsync(id);

            if (userEntity==null)
            {
                httpResponseResultModel.ErrorMessage = "用户信息不存在";
                return httpResponseResultModel;
            }
             
            if (!string.IsNullOrEmpty(updateDto.Password))
            {
                userEntity.Password = EncryptDecryptHelper.Md5(updateDto.Password.Trim());
            } 
            userEntity.Name=updateDto.Name;
            userEntity.MobilePhone=updateDto.MobilePhone;
            userEntity.EMail=updateDto.EMail;
            userEntity.Sex=updateDto.Sex;
            userEntity.Status=updateDto.Status;
            userEntity.HeadUrl=updateDto.HeadUrl;
            userEntity.AdminType = updateDto.AdminType;
             
            httpResponseResultModel.IsSuccess = await userService.UpdateAsync(userEntity);

            await roleUsersService.DeleteAsync(it => it.IsDeleted == false && it.UserID == userEntity.Id);

            if (updateDto.UserRoleList.Count > 0)
            {
                List<SysRoleUsersEntity> userRoleList = new List<SysRoleUsersEntity>();
                foreach (var item in updateDto.UserRoleList)
                {
                    if (!userRoleList.Exists(it => it.RoleID == item))
                    {
                        SysRoleUsersEntity entity = new SysRoleUsersEntity();
                        entity.Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                        entity.UserID = userEntity.Id;
                        entity.RoleID = item;
                        userRoleList.Add(entity);
                    }
                }

                if (userRoleList.Count > 0)
                {
                    await roleUsersService.InsertRangeAsync(userRoleList);
                }
            }

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

            var userEntity = await userService.GetByIdAsync(id);
            if (userEntity == null)
            {
                httpResponseResultModel.ErrorMessage = "用户不存在";
                return httpResponseResultModel;
            }
            if (userEntity.AdminType == (int)AccountTypeEnum.SuperAdministrator)
            {
                httpResponseResultModel.ErrorMessage = "超级用户不允许删除！";
                return httpResponseResultModel;
            }
            httpResponseResultModel.IsSuccess = await userService.SoftDeleteByIdAsync(id);
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
                httpResponseResultModel.ErrorMessage = "id为空！";
                return httpResponseResultModel;
            }
            if (userService.IsAny(it => it.IsDeleted == false && it.AdminType == (int)AccountTypeEnum.SuperAdministrator && idList.Contains(it.Id)))
            {
                httpResponseResultModel.ErrorMessage = "删除列表包含超级用户，超级用户不允许删除！";
                return httpResponseResultModel;
            }

            httpResponseResultModel.IsSuccess = await userService.SoftDeleteByIdsAsync(idList);
            httpResponseResultModel.BackResult = httpResponseResultModel.IsSuccess;
            return httpResponseResultModel;
        }

        /// <summary>
        /// 条件组装
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        private Expression<Func<SysUserEntity, bool>> PackageExpression(SysUserConditionDTO dto)
        {
            Expression<Func<SysUserEntity, bool>> expression = t => t.IsDeleted == false;
            if (!string.IsNullOrEmpty(dto.Account))
            {
                expression = expression.And(x => x.Account.Contains(dto.Account.Trim()));
            }
            if (!string.IsNullOrEmpty(dto.Name))
            {
                expression = expression.And(x => !string.IsNullOrEmpty(x.Name) && x.Name.Contains(dto.Name));
            }
            return expression;
        }

        /// <summary>
        /// 根据当前登录用户获取权限
        /// </summary>
        /// <returns></returns>
        public async Task<List<string>> GetPermissions()
        {
            long userId = CurrentUserInfo.Instance.UserID;

            List<string> permissions = new List<string>();

            if (CurrentUserInfo.Instance.AccountType == AccountTypeEnum.SuperAdministrator)
            {
                var menuDynamics = await menuService.GetDb().AsQueryable()
                        .InnerJoin<SysMenuButtonEntity>((menu, menuButton) => menu.IsDeleted == false && menuButton.IsDeleted == false
                        && menu.Id == menuButton.MenuID).Select((menu, menuButton) => new SysTempDto() { RouterName = menu.RouterName, Code = menuButton.Code }).ToListAsync();
                foreach (var item in menuDynamics)
                {
                    string permission = item.RouterName + "." + item.Code;
                    if (!permissions.Exists(i => i == permission))
                    {
                        permissions.Add(permission);
                    }
                }
            }
            else
            {
                var roleUserList = await roleUsersService.GetDb().AsQueryable()
                                    .InnerJoin<SysRoleEntity>((u, r) => u.RoleID == r.Id && u.IsDeleted == false && r.IsDeleted == false && r.Status == DataDictionary.Code.Status.正常)
                                    .Where(u => u.UserID == userId)
                                    .Select(u => u.RoleID).ToListAsync();

                var ls = await roleMenuService.GetDb().AsQueryable().InnerJoin<SysMenuEntity>((r, m) => r.MenuID == m.Id &&
              roleUserList.Contains(r.RoleID) && r.IsDeleted == false && m.IsDeleted == false && m.Status == DataDictionary.Code.Status.正常)
                  .Select((r, m) => new { RouterName = m.RouterName, MenuId = m.Id, MenuButtonCodes = r.MenuButtonCodes }).ToListAsync();
                if (ls != null && ls.Count > 0)
                {
                    //增加菜单按钮
                    var menuButtonlist = await menuButtonService.GetListAsync(it => it.IsDeleted == false && ls.Any(s => s.MenuId == it.MenuID));
                    foreach (var item in ls)
                    {
                        List<SysMenuButtonEntity> buttonList = new List<SysMenuButtonEntity>();

                        if (menuButtonlist != null && menuButtonlist.Count >= 0 && menuButtonlist.Exists(it => it.MenuID == item.MenuId))
                        {
                            buttonList = menuButtonlist.Where(it => it.MenuID == item.MenuId).ToList();
                        }

                        if (!string.IsNullOrEmpty(item.MenuButtonCodes))
                        {
                            List<string> buttonLs = JsonConvert.DeserializeObject<List<string>>(item.MenuButtonCodes.ToString());
                            if (buttonLs != null && buttonLs.Count > 0)
                            {
                                foreach (var button in buttonLs)
                                {
                                    if (buttonList == null || buttonList.Count <= 0 || !buttonList.Exists(it => it.Code == button))
                                    {
                                        continue;
                                    }
                                    string permission = item.RouterName + "." + button;
                                    if (!permissions.Exists(i => i == permission))
                                    {
                                        permissions.Add(permission);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return permissions;
        }

        /// <summary>
        /// 密码重置
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> PasswordReset(SysUserUpdateDTO dto)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var userEntity = userService.GetById(dto.ID);
            if (userEntity == null)
            {
                httpResponseResultModel.ErrorMessage = "用户不存在";
                return httpResponseResultModel;
            }

            userEntity.Password = EncryptDecryptHelper.Md5(dto.Password.Trim());
            httpResponseResultModel.IsSuccess = await userService.UpdateAsync(userEntity);
            return httpResponseResultModel;
        }

        /// <summary>
        /// 修改状态
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> UpdateStatus(SysUserUpdateDTO dto)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            var userEntity = userService.GetById(dto.ID);
            if (userEntity == null)
            {
                httpResponseResultModel.ErrorMessage = "用户不存在";
                return httpResponseResultModel;
            }
            if (userEntity.AdminType == (int)AccountTypeEnum.SuperAdministrator && dto.Status == DataDictionary.Code.Status.停用)
            {
                httpResponseResultModel.ErrorMessage = "超级用户不允许停用！";
                return httpResponseResultModel;
            }
            userEntity.Status = dto.Status;
            httpResponseResultModel.IsSuccess = await userService.UpdateAsync(userEntity);
            return httpResponseResultModel;
        }


        /// <summary>
        /// 修改密码
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> ChangePassword(UserChangePwdDTO dto)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            if (string.IsNullOrEmpty(dto.OldPwd))
            {
                httpResponseResultModel.ErrorMessage = "原密码不能为空！";
                return httpResponseResultModel;
            }
            if (string.IsNullOrEmpty(dto.NewPwd))
            {
                httpResponseResultModel.ErrorMessage = "新密码不能为空！";
                return httpResponseResultModel;
            }
            if (string.IsNullOrEmpty(dto.ConfirmPwd))
            {
                httpResponseResultModel.ErrorMessage = "确认密码不能为空！";
                return httpResponseResultModel;
            }
            //未加密密码
            string unencryptedPwd = dto.NewPwd.Trim();
            dto.OldPwd = EncryptDecryptHelper.Md5(dto.OldPwd.Trim());
            dto.NewPwd = EncryptDecryptHelper.Md5(dto.NewPwd.Trim());
            dto.ConfirmPwd = EncryptDecryptHelper.Md5(dto.ConfirmPwd.Trim());

            if (!dto.NewPwd.Equals(dto.ConfirmPwd))
            {
                httpResponseResultModel.ErrorMessage = "新密码与确认密码不一致！";
                return httpResponseResultModel;
            }
            if (dto.NewPwd.Equals(dto.OldPwd))
            {
                httpResponseResultModel.ErrorMessage = "新密码与原密码一致！";
                return httpResponseResultModel;
            }

            var userEntity = await GetAsync(CurrentUserInfo.Instance.UserID);

            if (userEntity == null)
            {
                httpResponseResultModel.ErrorMessage = "用户信息已失效！";
                return httpResponseResultModel;
            }

            if (!userEntity.Password.Equals(dto.OldPwd))
            {
                httpResponseResultModel.ErrorMessage = "原密码错误！";
                return httpResponseResultModel;
            }

            userEntity.Password = dto.NewPwd;
            httpResponseResultModel.IsSuccess = await userService.UpdateAsync(userEntity);

            return httpResponseResultModel;
        }


        /// <summary>
        /// 角色批量分配
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<bool>> SavePur(PermissionAllocationDTO dto)
        {
            HttpResponseResultModel<bool> httpResponseResultModel = new HttpResponseResultModel<bool>();
            if (dto.RoleIds.Count <= 0)
            {
                httpResponseResultModel.ErrorMessage = "角色列表不能为空";
                return httpResponseResultModel;
            }

            if (dto.UserIds.Count <= 0)
            {
                httpResponseResultModel.ErrorMessage = "用户列表不能为空";
                return httpResponseResultModel;
            }

            var userList = await userService.GetListAsync(it => dto.UserIds.Contains(it.Id));
            if (userList.Count <= 0)
            {
                httpResponseResultModel.ErrorMessage = "用户列表不能为空";
                return httpResponseResultModel;
            }

            try
            {
                var userIds = userList.Select(it => it.Id).ToList();
                var roleUserList = await roleUsersService.GetListAsync(it => it.IsDeleted == false && userIds.Contains(it.UserID.Value));
                using (var tran = roleUsersService.BeginTran())
                {
                    // await roleUsersService.UpdateAsync(it => new SysRoleUsersEntity { IsDeleted = true }, it => userIds.Contains(it.UserID.Value));

                    List<SysRoleUsersEntity> saveRoleUserList = new List<SysRoleUsersEntity>();

                    foreach (var user in userIds)
                    {
                        dto.RoleIds.ForEach(role =>
                        {
                            SysRoleUsersEntity roleUsersEntity = new SysRoleUsersEntity();
                            roleUsersEntity.UserID = user;
                            roleUsersEntity.RoleID = role;
                            if (!saveRoleUserList.Exists(x => x.RoleID == role && x.UserID == user) && !roleUserList.Exists(it => it.RoleID == role && it.UserID == user))
                            {
                                saveRoleUserList.Add(roleUsersEntity);
                            }
                        });
                    }
                    httpResponseResultModel.IsSuccess = await roleUsersService.InsertRangeAsync(roleUserList);
                }
                return httpResponseResultModel;
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }
    }
}
