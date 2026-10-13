<script setup lang="ts">
import apiRole from '@/api/modules/system/role'
import user from '@/api/modules/user'
import { ElMessage } from 'element-plus'
import { useI18n } from 'vue-i18n'
import messages from '../lang'

const props = withDefaults(
  defineProps<{ data: any }>(),
  {
    data: [],
  },
)
const { t, mergeLocaleMessage } = useI18n()
// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const from = ref({
  roleIds: [],
  empData: props.data,
  roleData: [],

})

const loading = ref(false)

onMounted(() => {
  apiRole.details({ status: 1 }).then((res: any) => {
    from.value.roleData = res
  })
})
defineExpose({
  submit() {
    return new Promise<void>((resolve) => {
      if (from.value.roleIds == null || from.value.roleIds.length <= 0) {
        ElMessage.error(t('user_form.selectRoleRequired'))
        return
      }
      if (from.value.empData.length <= 0) {
        ElMessage.error(t('user_form.empListNotEmpty'))
        return
      }

      loading.value = true
      const empIds = from.value.empData.map(item => item.id)
      user.savePur({ RoleIds: from.value.roleIds, UserIds: empIds }).then((res: any) => {
        ElMessage.success(t('common.saveSuccess'))
        resolve()
      }).finally(() => {
        loading.value = false
      })
    })
  },
})
</script>

<template>
  <div>
    <FaPageMain v-loading="loading">
      <ElRow :gutter="16">
        <ElCol :span="8">
          <ElCard>
            <template #header>
              <div class="card-header">
                <span>{{ $t('user_form.userList') }}</span>
              </div>
            </template>
            <el-table :data="from.empData" height="350" style="width: 100%;" border>
              <el-table-column prop="name" :label="$t('user_form.name')" />
            </el-table>
          </ElCard>
        </ElCol>
        <ElCol :span="16">
          <ElCard :title="$t('user_form.role')">
            <template #header>
              <div class="card-header">
                <span>{{ $t('user_form.roleList') }}</span>
              </div>
            </template>
            <el-checkbox-group v-model="from.roleIds">
              <el-checkbox v-for="role in from.roleData" :key="role.id" :label="role.id">
                {{ role.name }}
              </el-checkbox>
            </el-checkbox-group>
          </ElCard>
        </ElCol>
      </ElRow>
    </FaPageMain>
  </div>
</template>
