<route lang="yaml">
meta:
  enabled: false
</route>

<script setup lang="ts">
import type Node from 'element-plus/es/components/tree/src/model/node'
import apiMenu from '@/api/modules/system/menu'
import apiSystem from '@/api/modules/system/system'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import DetailForm from './components/DetailForm/index.vue'
import messages from './components/lang'
defineOptions({
  name: 'MenuList',
})

const { t, mergeLocaleMessage } = useI18n()

const router = useRouter()

const routerName = ref(router.currentRoute.value.name.toString())

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}
const menuTreeRef = ref()
const menu = ref([])
const systemOptions = ref([])
const systemId = ref('')
const form = ref({
  visible: false,
  id: '',
  parentId: '',
})

function getMenuList() {
  if (systemId.value === '' || systemId.value === undefined || systemId.value === null) {
    menu.value = []
    return
  }
  apiMenu.getTreetList({ systemId: systemId.value }).then((res: any) => {
    menu.value = res
    nextTick(() => {
      menuTreeRef.value.setCurrentKey(form.value.id)
    })
  })
}
onMounted(() => {
  apiSystem.details({}).then((res: any) => {
    systemOptions.value = res
  })

  // getMenuList()
})

function menuAdd(data: any) {
  form.value = {
    visible: false,
    id: '',
    parentId: '',
  }
  nextTick(() => {
    form.value = {
      visible: true,
      id: '',
      parentId: data.id,
    }
  })
}
function menuEdit(data?: any) {
  if (!data && systemId.value === '') {
    ElMessage({
      message: t('menu_form.selectSystem'),
      type: 'warning',
    })
    return
  }
  form.value = {
    visible: false,
    id: '',
    parentId: '',
  }
  nextTick(() => {
    form.value = {
      visible: true,
      id: data?.id ?? '',
      parentId: data?.parentId ?? '',
    }
  })
}
function menuDelete(node: Node, data: any) {
  ElMessageBox.confirm(t('menu_form.deleteConfirm', { name: data.menuName }), t('menu_form.confirmInfo')).then(() => {
    apiMenu.delete(data.id).then(() => {
      ElMessage.success({
        message: t('menu_form.mockDeleteSuccess'),
        center: true,
      })
      const parent = node.parent
      const children = parent.data.children || parent.data
      const index = children.findIndex((d: any) => d.id === data.id)
      children.splice(index, 1)
      menu.value = [...menu.value]
      form.value = {
        visible: false,
        id: '',
        parentId: '',
      }
    })
  })
}
function menuSubmit() {
  // form.value = {
  //   visible: false,
  //   id: '',
  //   parentId: '',
  // }
  getMenuList()
}

function onClose() {
  form.value = {
    visible: false,
    id: '',
    parentId: '',
  }
}
</script>

<template>
  <div class="absolute size-full flex flex-col">
    <div class="relative flex flex-1 flex-col overflow-auto">
      <FaLayoutContainer hide-left-side-toggle class="static" left-side-class="h-full flex flex-col gap-4" default-class="flex-col-center p-0 size-full">
        <template #leftSide>
          <ElSelect
            v-model="systemId"
            :placeholder="t('common.pleaseSelect')"
            clearable
            @change="getMenuList"
          >
            <el-option
              v-for="item in systemOptions"
              :key="item.id"
              :label="item.name"
              :value="item.id"
            />
          </ElSelect>
          <ElButtonGroup class="inline-flex">
            <ElButton type="primary" class="w-full" @click="menuEdit()">
              {{ t('menu_form.addMenu') }}
            </ElButton>
            <ElButton @click="getMenuList">
              <template #icon>
                <FaIcon name="i-ep:refresh" />
              </template>
            </ElButton>
          </ElButtonGroup>
          <ElScrollbar class="tree">
            <ElTree ref="menuTreeRef" :data="menu" default-expand-all :props="{ label: 'menuName' }" node-key="id" @node-click="menuEdit">
              <template #default="{ node, data }">
                <div class="custom-tree-node">
                  <div class="label" :title="data.menuName">
                    <FaIcon v-if="data.menuIcon" :name="data.menuIcon" class="size-4" style="margin-inline-end: 5px;" />
                    <div class="text">
                      {{ data.menuName }}
                    </div>
                  </div>
                  <div class="actions">
                    <ElButtonGroup v-auth="[`${routerName}.add`, `${routerName}.edit`, `${routerName}.delete`]">
                      <ElButton v-auth="`${routerName}.add`" type="primary" plain size="small" @click.stop="menuAdd(data)">
                        <template #icon>
                          <FaIcon name="i-ep:plus" />
                        </template>
                      </ElButton>
                      <ElButton v-auth="`${routerName}.edit`" type="info" plain size="small" @click.stop="menuEdit(data)">
                        <template #icon>
                          <FaIcon name="i-ep:edit" />
                        </template>
                      </ElButton>
                      <ElButton v-auth="`${routerName}.delete`" type="danger" plain size="small" @click.stop="menuDelete(node, data)">
                        <template #icon>
                          <FaIcon name="i-ep:delete" />
                        </template>
                      </ElButton>
                    </ElButtonGroup>
                  </div>
                </div>
              </template>
            </ElTree>
          </ElScrollbar>
        </template>
        <DetailForm v-if="form.visible" :id="form.id" :parent-id="form.parentId" :system-id="systemId" @on-submit="menuSubmit" @on-close="onClose" />
        <div v-else class="flex-center flex-1 text-3xl text-primary/50">
          {{ t("menu_form.text") }}
        </div>
      </FaLayoutContainer>
    </div>
  </div>
</template>

<style scoped>
.tree {
  flex: 1;
  overflow-y: auto;

  :deep(.el-tree) {
    .el-tree-node__content {
      height: 40px;
    }

    .is-current > .el-tree-node__content {
      background-color: var(--el-color-primary-light-9);
    }

    .custom-tree-node {
      position: relative;
      display: flex;
      flex: 1;
      flex-direction: column;
      justify-content: center;
      width: 0;
      height: 100%;

      .label {
        display: inline-flex;
        align-items: center;
        width: calc(100% - 10px);
        color: var(--el-text-color-primary);

        .text {
          overflow: hidden;
          text-overflow: ellipsis;
          white-space: nowrap;
        }
      }

      &:hover {
        .actions {
          display: block;
        }
      }

      .actions {
        position: absolute;
        top: 50%;
        right: 10px;
        display: none;
        transform: translateY(-50%);

        .el-button {
          padding: 5px 8px;
        }
      }
    }
  }
}
</style>
