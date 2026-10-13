<script setup lang="ts">
import type { FormInstance, FormRules } from 'element-plus'
import { ElMessage } from 'element-plus'
import { useI18n } from 'vue-i18n'
import apiRole from '@/api/modules/system/role'
import apiManager from '@/api/modules/user'

export interface Props {
  id?: number | string
}
const props = withDefaults(
  defineProps<Props>(),
  {
    id: '',
  },
)
const { t } = useI18n()
const roleList = ref<any>([])
apiRole.details({ status: 1 }).then((res) => {
  roleList.value = res
})

const loading = ref(false)
const formRef = useTemplateRef<FormInstance>('formRef')
const form = ref({
  id: props.id,
  account: '',
  password: '',
  name: '',
  mobilePhone: '',
  eMail: '',
  positionName: '',
  oUID: '',
  sex: 3,
  adminType: 1,
  status: 1,
  userRoleList: [],
})
const formRules = ref<FormRules>({
  account: [
    { required: true, message: t('common.pleaseInput') + t('user_form.account'), trigger: 'blur' },
  ],
  name: [
    { required: true, message: t('common.pleaseInput') + t('user_form.name'), trigger: 'blur' },
  ],
  mobilePhone: [
    { required: true, message: t('common.pleaseInput') + t('user_form.mobilePhone'), trigger: 'blur' },
  ],
})

onMounted(() => {
  if (form.value.id !== '') {
    getInfo()
  }
})

function getInfo() {
  loading.value = true
  apiManager.getEntity(form.value.id).then((res: any) => {
    form.value = res
  }).finally(() => {
    loading.value = false
  })
}

defineExpose({
  submit() {
    return new Promise<void>((resolve) => {
      formRef.value?.validate((valid) => {
        if (valid) {
          if (form.value.id === '') {
            apiManager.create(form.value).then(() => {
              ElMessage.success({
                message: t('common.createSuccess'),
                center: true,
              })
              resolve()
            })
          }
          else {
            apiManager.edit(form.value).then(() => {
              ElMessage.success({
                message: t('common.updateSuccess'),
                center: true,
              })
              resolve()
            })
          }
        }
      })
    })
  },
})
</script>

<template>
  <div v-loading="loading">
    <ElForm ref="formRef" :model="form" :rules="formRules" label-width="120px" label-suffix="：">
      <FaPageMain :title="t('user_form.baseInfo')">
        <ElRow>
          <ElCol :span="12">
            <ElFormItem :label="t('user_form.name')" prop="name">
              <ElInput v-model="form.name" :placeholder="t('common.pleaseInput') + t('user_form.name')" :maxlength="50" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="12">
            <ElFormItem :label="t('user_form.mobilePhone')" prop="mobilePhone">
              <ElInput v-model="form.mobilePhone" :placeholder="t('common.pleaseInput') + t('user_form.mobilePhone')" :maxlength="13" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="12">
            <ElFormItem :label="t('user_form.eMail')" prop="eMail">
              <ElInput v-model="form.eMail" :placeholder="t('common.pleaseInput') + t('user_form.eMail')" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="12">
            <ElFormItem :label="t('user_form.sex')" prop="sex">
              <ElRadioGroup v-model="form.sex">
                <ElRadioButton :value="3">
                  {{ t('common.secrecy') }}
                </ElRadioButton>
                <ElRadioButton :value="2">
                  {{ t('common.man') }}
                </ElRadioButton>
                <ElRadioButton :value="1">
                  {{ t('common.girl') }}
                </ElRadioButton>
              </ElRadioGroup>
            </ElFormItem>
          </ElCol>
          <ElCol :span="12">
            <ElFormItem :label="t('user_form.positionName')" prop="positionName">
              <ElInput v-model="form.positionName" :placeholder="t('common.pleaseInput') + t('user_form.positionName')" :maxlength="50" />
            </ElFormItem>
          </ElCol>
        </ElRow>
      </FaPageMain>
      <FaPageMain :title="t('user_form.accountInfo')">
        <ElRow>
          <ElCol :span="12">
            <ElFormItem :label="t('user_form.account')" prop="account">
              <ElInput v-model="form.account" :disabled="form.id != '' && form.account != '' && form.account != undefined" :placeholder="t('common.pleaseInput') + t('user_form.account')" :maxlength="20" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="12">
            <ElFormItem :label="t('user_form.password')" prop="password">
              <ElInput v-model="form.password" type="password" show-password :placeholder="t('common.pleaseInput') + t('user_form.password')" :maxlength="50" />
            </ElFormItem>
          </ElCol>
          <ElCol v-if="form.adminType !== 999" :span="12">
            <ElFormItem :label="t('user_form.adminType')" prop="adminType">
              <ElRadioGroup v-model="form.adminType">
                <ElRadioButton :value="1">
                  {{ t('user_form.ordinaryUsers') }}
                </ElRadioButton>
                <ElRadioButton :value="2">
                  {{ t('user_form.systemUsers') }}
                </ElRadioButton>
              </ElRadioGroup>
            </ElFormItem>
          </ElCol>
          <ElCol v-if="form.adminType !== 999" :span="12">
            <ElFormItem :label="t('user_form.status')" prop="status">
              <ElRadioGroup v-model="form.status">
                <ElRadioButton :value="1">
                  {{ t('common.normal') }}
                </ElRadioButton>
                <ElRadioButton :value="2">
                  {{ t('common.stopUsing') }}
                </ElRadioButton>
              </ElRadioGroup>
            </ElFormItem>
          </ElCol>
        </ElRow>
      </FaPageMain>
      <FaPageMain :title="t('user_form.permission')">
        <el-checkbox-group
          v-model="form.userRoleList"
        >
          <el-checkbox v-for="item in roleList" :key="item.id" :label="item.name" :value="item.id">
            {{ item.name }}
          </el-checkbox>
        </el-checkbox-group>
      </FaPageMain>
    </ElForm>
  </div>
</template>
