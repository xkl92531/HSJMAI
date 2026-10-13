<script setup lang="ts">
import type { FormInstance, FormRules } from 'element-plus'
import { ElInputNumber, ElMessage } from 'element-plus'
import Sortable from 'sortablejs'
import { useI18n } from 'vue-i18n'
import apiMenu from '@/api/modules/system/menu'
import messages from '../lang'

const props = withDefaults(
  defineProps<{
    id?: string | number
    parentId?: string | number
    systemId?: string | number
  }>(),
  {
    id: '',
    parentId: '',
    systemId: '',
  },
)

const emits = defineEmits<{
  onSubmit: []
  onClose: []
}>()
const menuTreeData = ref()
const defaultProps = {
  children: 'children',
  label: 'menuName',
}
const router = useRouter()

const routerName = ref(router.currentRoute.value.name.toString())

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}
const loading = ref(false)
const formRef = useTemplateRef<FormInstance>('formRef')
const form = ref({
  id: props.id,
  parentId: props.parentId == undefined ? '' : props.parentId,
  systemID: props.systemId,
  menuPath: '',
  menuRedirect: '',
  routerName: '',
  menuComponent: '',
  menuName: '',
  menuIcon: 'ant-design:appstore-add-outlined',
  menuActiveIcon: 'ant-design:appstore-add-outlined',
  menuDefaultOpened: false,
  menuAlwaysOpened: false,
  menuPermanent: false,
  menuDisplay: true,
  menuActive: '',
  enableCache: false,
  noCacheList: [] as string[],
  menuBadge: false,
  menuBadgeVariant: 'default' as 'default' | 'secondary' | 'destructive',
  menuNewWindow: false,
  link: '',
  menuIframe: '',
  menuCopyright: true,
  menuBreadcrumb: true,
  buttonList: [] as { name: string, code: string }[],
  menuSingle: false,
  menuMaximize: false,
  isWhitelist: false,
  status: 1,
  sort: 1,
  menuI18n: '',
})
const formRules = ref<FormRules>({
  menuPath: [
    { required: true, message: t('menu_form.menuPathPlaceholder'), trigger: 'blur' },
  ],
  menuName: [
    { required: true, message: t('menu_form.menuNamePlaceholder'), trigger: 'blur' },
  ],
  routerName: [
    { required: true, message: t('menu_form.routerNamePlaceholder'), trigger: 'blur' },
  ],
})

onMounted(() => {
  apiMenu.getTreeChildList().then((res) => {
    menuTreeData.value = res
  })
  if (form.value.id !== '') {
    getInfo()
  }
  else {
    apiMenu.getSort({ menuId: (props.parentId !== '' ? props.parentId : '0'), systemId: props.systemId }).then((res: any) => {
      form.value.sort = res
    })
    if (props.parentId !== '' && props.parentId != '0') {
      form.value.buttonList.push({ name: t('common.add'), code: 'add' })
      form.value.buttonList.push({ name: t('common.edit'), code: 'edit' })
      form.value.buttonList.push({ name: t('common.delete'), code: 'delete' })
      form.value.buttonList.push({ name: t('common.export'), code: 'export' })
      form.value.buttonList.push({ name: t('common.import'), code: 'import' })
    }
  }
})

function getInfo() {
  loading.value = true
  apiMenu.getEntity(form.value.id).then((res: any) => {
    loading.value = false
    form.value = res
  })
}

// 不缓存规则
const inputNoCache = ref('')
const inputNoCacheVisible = ref(false)
const inputNoCacheRef = useTemplateRef('inputNoCacheRef')
function onInputNoCacheClose(tag: string) {
  form.value.noCacheList.splice(form.value.noCacheList.indexOf(tag), 1)
}
function onInputNoCacheShow() {
  inputNoCacheVisible.value = true
  nextTick(() => {
    inputNoCacheRef.value?.input?.focus()
  })
}
function onInputNoCacheConfirm() {
  if (inputNoCache.value) {
    if (!form.value.noCacheList.includes(inputNoCache.value)) {
      form.value.noCacheList.push(inputNoCache.value)
    }
    else {
      ElMessage.warning(t('menu_form.ruleExists'))
    }
  }
  inputNoCacheVisible.value = false
  inputNoCache.value = ''
}

const authsTableRef = useTemplateRef('authsTableRef')
const authsTableKey = ref(0)
onMounted(() => {
  onAuthDarg()
})
function onAuthAdd() {
  form.value.buttonList.push({
    name: '',
    code: '',
  })
  nextTick(() => {
    authsTableRef.value?.setScrollTop(form.value.buttonList.length * 50)
  })
}
function onAuthDelete(index: number) {
  form.value.buttonList.splice(index, 1)
}
function onAuthDarg() {
  const tbody = authsTableRef.value?.$el.querySelector('.el-table__body-wrapper tbody')
  Sortable.create(tbody, {
    handle: '.sortable',
    animation: 300,
    ghostClass: 'ghost',
    onEnd: ({ newIndex, oldIndex }) => {
      if (newIndex === undefined || oldIndex === undefined) {
        return
      }
      const currRow = form.value.buttonList.splice(oldIndex, 1)[0]
      form.value.buttonList.splice(newIndex, 0, currRow)
      authsTableKey.value += 1
      nextTick(() => onAuthDarg())
    },
  })
}
const filterMenu = (value: any, data: { menuName: string | any[] }) => data.menuName.includes(value)
function onSubmit() {
  formRef.value?.validate((valid) => {
    if (valid) {
      if (form.value.id === '') {
        apiMenu.create(form.value).then((res: any) => {
          ElMessage.success({
            message: t('menu_form.addSuccess'),
            center: true,
          })
          form.value.id = res.backResult
          getInfo()
          emits('onSubmit')
        })
      }
      else {
        apiMenu.edit(form.value).then(() => {
          ElMessage.success({
            message: t('common.saveSuccess'),
            center: true,
          })
          getInfo()
          emits('onSubmit')
        })
      }
    }
  })
}

function onClose() {
  emits('onClose')
}
</script>

<template>
  <div class="form">
    <div v-loading="loading" class="top-side">
      <ElForm ref="formRef" :model="form" :rules="formRules" label-position="top">
        <div class="form-left-side">
          <ElScrollbar>
            <FaPageHeader v-if="!!form.parentId && form.parentId != '0' " :title="t('menu_form.basicConfiguration')" :description="t('menu_form.basicConfigurationDesc')" class="mb-0 border-b-none bg-border/50" />
            <ElRow v-if="!!form.parentId && form.parentId != '0'" :gutter="30" style="padding: 20px;">
              <ElCol :xl="12" :lg="24">
                <ElFormItem :label="t('menu_form.menuPath')" prop="menuPath">
                  <ElInput v-model="form.menuPath" clearable :placeholder="t('menu_form.menuPathPlaceholder')" />
                </ElFormItem>
              </ElCol>
              <ElCol :xl="12" :lg="24">
                <ElFormItem :label="t('menu_form.menuRedirect')" prop="menuRedirect">
                  <ElInput v-model="form.menuRedirect" clearable :placeholder="t('menu_form.menuRedirectPlaceholder')" />
                </ElFormItem>
              </ElCol>
              <ElCol :xl="12" :lg="24">
                <ElFormItem prop="routerName">
                  <template #label>
                    {{ t('menu_form.routerName') }}
                    <span class="label-tip">{{ t('menu_form.routerNameTip') }}</span>
                  </template>
                  <ElInput v-model="form.routerName" clearable :placeholder="t('menu_form.routerNamePlaceholder')" />
                </ElFormItem>
              </ElCol>
              <ElCol :xl="12" :lg="24">
                <ElFormItem prop="menuComponent">
                  <template #label>
                    {{ t('menu_form.menuComponent') }}
                    <span class="label-tip">
                      {{ t('menu_form.menuComponentTipPrefix') }}<ElLink type="primary" underline="hover" @click.prevent="form.menuComponent = 'Layout'">Layout</ElLink>{{ t('menu_form.menuComponentTipSuffix') }}
                    </span>
                  </template>
                  <ElInput v-model="form.menuComponent" clearable :placeholder="t('menu_form.menuComponentPlaceholder')">
                    <template v-if="form.menuComponent !== 'Layout'" #prepend>
                      /src/views/
                    </template>
                  </ElInput>
                </ElFormItem>
              </ElCol>
            </ElRow>
            <FaPageHeader :title="t('menu_form.expandConfiguration')" class="mb-0 border-b-none bg-border/50" />
            <ElRow :gutter="30" style="padding: 20px;">
              <ElCol :xl="12" :lg="24">
                <ElFormItem :label="t('menu_form.menuName')" prop="menuName">
                  <ElInput v-model="form.menuName" clearable :placeholder="t('menu_form.menuNamePlaceholder')" />
                </ElFormItem>
              </ElCol>
              <ElCol :xl="12" :lg="24">
                <ElFormItem prop="parentId" :label="t('menu_form.parentMenu')">
                  <el-tree-select
                    v-model="form.parentId" :placeholder="t('menu_form.parentMenuPlaceholder')" value-key="id" :data="menuTreeData"
                    :props="defaultProps" :render-after-expand="false" :default-expand-all="true"
                    :filter-node-method="filterMenu" filterable clearable check-strictly
                  />
                </ElFormItem>
              </ElCol>
              <ElCol :xl="12" :lg="24">
                <ElFormItem :label="t('menu_form.sort')" prop="sort">
                  <ElInputNumber v-model="form.sort" />
                </ElFormItem>
              </ElCol>
              <ElCol :xl="12" :lg="24">
                <ElFormItem :label="t('menu_form.menuI18n')" prop="menuI18n">
                  <ElInput v-model="form.menuI18n" clearable :placeholder="t('menu_form.menuI18nPlaceholder')" />
                </ElFormItem>
              </ElCol>
              <ElCol :xl="12" :lg="24">
                <ElFormItem :label="t('menu_form.status')" prop="status">
                  <ElSwitch v-model="form.status" inline-prompt :active-text="t('common.normal')" :active-value="1" :inactive-value="2" :inactive-text="t('common.stopUsing')" />
                </ElFormItem>
              </ElCol>
              <ElCol :xl="12" :lg="24">
                <ElFormItem :label="t('menu_form.menuIcon')" prop="menuIcon">
                  <FaIconPicker v-model="form.menuIcon" />
                </ElFormItem>
              </ElCol>
              <ElCol :xl="12" :lg="24">
                <ElFormItem :label="t('menu_form.menuActiveIcon')" prop="menuActiveIcon">
                  <FaIconPicker v-model="form.menuActiveIcon" />
                </ElFormItem>
              </ElCol>

              <ElCol v-if="!!form.parentId && form.parentId != '0'" :xl="12" :lg="24">
                <ElFormItem :label="t('menu_form.menuDefaultOpened')" prop="menuDefaultOpened">
                  <ElSwitch v-model="form.menuDefaultOpened" inline-prompt :active-text="t('common.yes')" :inactive-text="t('common.no')" />
                </ElFormItem>
              </ElCol>
              <ElCol v-if="!!form.parentId && form.parentId != '0'" :xl="12" :lg="24">
                <ElFormItem :label="t('menu_form.menuBreadcrumb')" prop="menuBreadcrumb">
                  <ElSwitch v-model="form.menuBreadcrumb" inline-prompt :active-text="t('menu_form.show')" :inactive-text="t('menu_form.hide')" />
                </ElFormItem>
              </ElCol>
              <ElCol v-if="!!form.parentId && form.parentId != '0'" :xl="12" :lg="24">
                <ElFormItem :label="t('menu_form.menuAlwaysOpened')" prop="menuAlwaysOpened">
                  <ElSwitch v-model="form.menuAlwaysOpened" inline-prompt :active-text="t('common.yes')" :inactive-text="t('common.no')" />
                </ElFormItem>
              </ElCol>
              <ElCol v-if="!!form.parentId && form.parentId != '0'" :xl="12" :lg="24">
                <ElFormItem :label="t('menu_form.menuBadge') " prop="menuBadge">
                  <ElSwitch v-model="form.menuBadge" inline-prompt :active-text="t('menu_form.show')" :inactive-text="t('menu_form.hide')" />
                </ElFormItem>
              </ElCol>
              <ElCol v-if="!!form.parentId && form.parentId != '0'" :xl="12" :lg="24">
                <ElFormItem :label="t('menu_form.menuBadgeVariant') " prop="menuBadgeVariant">
                  <ElRadioGroup v-model="form.menuBadgeVariant">
                    <ElRadioButton :label="t('menu_form.badgeVariantDefault')" value="default" />
                    <ElRadioButton :label="t('menu_form.badgeVariantSecondary')" value="secondary" />
                    <ElRadioButton :label="t('menu_form.badgeVariantDestructive')" value="destructive" />
                  </ElRadioGroup>
                </ElFormItem>
              </ElCol>
              <ElCol v-if="!!form.parentId && form.parentId != '0'" :xl="12" :lg="24">
                <ElFormItem :label="t('menu_form.menuDisplay') " prop="menuDisplay">
                  <ElSwitch v-model="form.menuDisplay" inline-prompt :active-text="t('menu_form.show')" :inactive-text="t('menu_form.hide')" />
                </ElFormItem>
              </ElCol>
              <ElCol v-if="!!form.parentId && form.parentId != '0'" :xl="12" :lg="24">
                <ElFormItem prop="menuPermanent">
                  <template #label>
                    {{ t('menu_form.menuPermanent') }}
                    <span class="label-tip">{{ t('menu_form.menuPermanentTip') }}</span>
                  </template>
                  <ElSwitch v-model="form.menuPermanent" inline-prompt :active-text="t('common.yes')" :inactive-text="t('common.no')" />
                </ElFormItem>
              </ElCol>
              <ElCol v-if="!!form.parentId && form.parentId != '0'" :xl="12" :lg="24">
                <ElFormItem :label="t('menu_form.menuSingle')" prop="menuSingle">
                  <template #label>
                    {{ t('menu_form.menuSingle') }}
                    <span class="label-tip">{{ t('menu_form.menuSingleTip') }}</span>
                  </template>
                  <ElSwitch v-model="form.menuSingle" inline-prompt :active-text="t('common.yes')" :inactive-text="t('common.no')" />
                </ElFormItem>
              </ElCol>

              <ElCol v-if="!!form.parentId && form.parentId != '0'" :xl="12" :lg="24">
                <ElFormItem :label="t('menu_form.enableCache')" prop="enableCache">
                  <ElSwitch v-model="form.enableCache" inline-prompt :active-text="t('menu_form.enableCache')" :inactive-text="t('menu_form.noCacheText')" />
                </ElFormItem>
              </ElCol>
              <ElCol v-if="!!form.parentId && form.parentId != '0'" :xl="12" :lg="24">
                <ElFormItem prop="noCache">
                  <template #label>
                    {{ t('menu_form.noCache') }}
                    <ElTooltip :content="t('menu_form.noCacheTip')" placement="top">
                      <FaIcon name="i-ri:question-line" />
                    </ElTooltip>
                  </template>
                  <ElSpace>
                    <ElTag v-for="item in (form.noCacheList as string[])" :key="item" size="large" closable :disable-transitions="false" @close="onInputNoCacheClose(item)">
                      {{ item }}
                    </ElTag>
                    <ElInput v-if="inputNoCacheVisible" ref="inputNoCacheRef" v-model="inputNoCache" style="width: 200px;" @keyup.enter="onInputNoCacheConfirm" @blur="onInputNoCacheConfirm" />
                    <ElButton v-else @click="onInputNoCacheShow">
                      {{ t('common.add') }}
                    </ElButton>
                  </ElSpace>
                </ElFormItem>
              </ElCol>
              <ElCol v-if="!!form.parentId && form.parentId != '0'" :xl="12" :lg="24">
                <ElFormItem prop="menuActive">
                  <template #label>
                    {{ t('menu_form.menuActive') }}
                    <ElTooltip :content="t('menu_form.menuActiveTip')" placement="top">
                      <FaIcon name="i-ri:question-line" />
                    </ElTooltip>
                  </template>
                  <ElInput v-model="form.menuActive" clearable :placeholder="t('menu_form.menuActivePlaceholder')" />
                </ElFormItem>
              </ElCol>
              <ElCol v-if="!!form.parentId && form.parentId != '0'" :xl="12" :lg="24">
                <ElFormItem :label="t('menu_form.menuNewWindow') " prop="menuNewWindow">
                  <ElSwitch v-model="form.menuNewWindow" inline-prompt :active-text="t('common.yes')" :inactive-text="t('common.no')" />
                </ElFormItem>
              </ElCol>
              <ElCol v-if="!!form.parentId && form.parentId != '0'" :xl="12" :lg="24">
                <ElFormItem :label="t('menu_form.menuMaximize') " prop="menuMaximize">
                  <ElSwitch v-model="form.menuMaximize" inline-prompt :active-text="t('common.yes')" :inactive-text="t('common.no')" />
                </ElFormItem>
              </ElCol>
              <ElCol v-if="!!form.parentId && form.parentId != '0'" :xl="12" :lg="24">
                <ElFormItem :label="t('menu_form.isWhitelist')" prop="isWhitelist">
                  <ElSwitch v-model="form.isWhitelist" inline-prompt :active-text="t('common.yes')" :inactive-text="t('common.no')" />
                </ElFormItem>
              </ElCol>
              <ElCol v-if="!!form.parentId && form.parentId != '0'" :xl="12" :lg="24">
                <ElFormItem prop="link">
                  <template #label>
                    {{ t('menu_form.link') }}
                    <ElTooltip :content="t('menu_form.linkTip')" placement="top">
                      <FaIcon name="i-ri:question-line" />
                    </ElTooltip>
                  </template>
                  <ElInput v-model="form.link" clearable :placeholder="t('menu_form.linkPlaceholder')" />
                </ElFormItem>
              </ElCol>
              <ElCol v-if="!!form.parentId && form.parentId != '0'" :xl="12" :lg="24">
                <ElFormItem prop="meta.iframe">
                  <template #label>
                    {{ t('menu_form.menuIframe') }}
                    <ElTooltip :content="t('menu_form.menuIframeTip')" placement="top">
                      <FaIcon name="i-ri:question-line" />
                    </ElTooltip>
                  </template>
                  <ElInput v-model="form.menuIframe" clearable :placeholder="t('menu_form.menuIframePlaceholder')" />
                </ElFormItem>
              </ElCol>
              <ElCol v-if="!!form.parentId && form.parentId != '0'" :xl="12" :lg="24">
                <ElFormItem :label="t('menu_form.menuCopyright')" prop="menuCopyright">
                  <ElSwitch v-model="form.menuCopyright" inline-prompt :active-text="t('menu_form.show')" :inactive-text="t('menu_form.hide')" />
                </ElFormItem>
              </ElCol>
            </ElRow>
          </ElScrollbar>
        </div>
        <div class="form-right-side">
          <FaPageHeader :title="t('menu_form.permissionPool')" class="mb-0 border-b-none bg-border/50">
            <template #description>
              <p>{{ t('menu_form.permissionPoolDesc1') }}</p>
              <p>{{ t('menu_form.permissionPoolDesc2') }}</p>
            </template>
          </FaPageHeader>
          <ElTable ref="authsTableRef" :key="authsTableKey" :data="form.buttonList" stripe highlight-current-row border>
            <ElTableColumn width="60" align="center" fixed>
              <template #header>
                <ElButton type="primary" size="small" circle plain @click="onAuthAdd">
                  <template #icon>
                    <FaIcon name="i-ep:plus" />
                  </template>
                </ElButton>
              </template>
              <template #default="scope">
                <span class="index">{{ scope.$index + 1 }}</span>
                <ElButton v-auth="`${routerName}.deleteauth`" type="danger" size="small" plain circle class="delete" @click="onAuthDelete(scope.$index)">
                  <template #icon>
                    <FaIcon name="i-ep:delete" />
                  </template>
                </ElButton>
              </template>
            </ElTableColumn>
            <ElTableColumn width="80" align="center" fixed>
              <template #header>
                {{ t('menu_form.sort') }}
              </template>
              <template #default>
                <ElTag type="info" class="sortable">
                  <FaIcon name="i-ep:d-caret" />
                </ElTag>
              </template>
            </ElTableColumn>
            <ElTableColumn :label="t('menu_form.authName')">
              <template #default="scope">
                <ElInput v-model="scope.row.name" />
              </template>
            </ElTableColumn>
            <ElTableColumn :label="t('menu_form.authCode')">
              <template #default="scope">
                <ElInput v-model="scope.row.code" />
              </template>
            </ElTableColumn>
          </ElTable>
        </div>
      </ElForm>
    </div>
    <div v-auth="`${routerName}.submit`" class="bottom-side">
      <ElButton type="primary" @click="onSubmit">
        {{ t('common.submit') }}
      </ElButton>
      <ElButton @click="onClose">
        {{ t('common.close') }}
      </ElButton>
    </div>
  </div>
</template>

<style scoped>
.form {
  position: relative;
  display: flex;
  flex: 1;
  flex-direction: column;
  width: 100%;
  overflow: auto;

  .top-side {
    display: flex;
    flex: 1;
    height: 0;

    .el-form {
      display: flex;
      flex: 1;
    }

    .form-left-side {
      flex: 1;
      padding: 16px;
      overflow: auto;

      .label-tip {
        display: inline-flex;
        margin-left: 10px;
        color: var(--el-text-color-disabled);
      }

      :deep(.el-scrollbar__view) {
        overflow-x: hidden;
      }
    }

    .form-right-side {
      --uno: border-l border-l-dashed;

      display: flex;
      flex-direction: column;
      width: 500px;
      padding: 16px;

      :deep(.el-table) {
        height: 100%;
        margin-top: 15px;

        .el-table__row {
          &.ghost {
            opacity: 0;
          }

          .index {
            display: inline-block;
          }

          .delete {
            display: none;
          }

          &:hover {
            .index {
              display: none;
            }

            .delete {
              display: inline-block;
            }
          }

          .el-tag.sortable,
          .el-tag.sortable .icon {
            cursor: ns-resize;
          }
        }
      }
    }
  }

  .bottom-side {
    --uno: border-t border-t-dashed;

    display: flex;
    justify-content: center;
    padding: 16px;
  }
}
</style>
