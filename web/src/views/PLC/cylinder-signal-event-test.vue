<route lang="yaml">
meta:
  title: 气缸信号采集任务
</route>

<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import CylinderSignalEventTester from './components/CylinderSignalEventTester.vue'
import messages from './cylinder-signal-event-test.lang'

defineOptions({
  name: 'PlcCylinderSignalEventTest',
})

const route = useRoute()
const router = useRouter()

const { t, mergeLocaleMessage } = useI18n()
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const projectId = computed(() => route.query.id as string || '')
const pageTitle = computed(() => projectId.value ? t('cylinderSignalEventTest.eventTestTitle') : t('cylinderSignalEventTest.collectTitle'))
const pageDesc = computed(() => projectId.value ? t('cylinderSignalEventTest.projectIdDesc', { id: projectId.value }) : t('cylinderSignalEventTest.allProjectsDesc'))

function handleOpenProjectDetail() {
  if (!projectId.value) {
    ElMessage.warning(t('cylinderSignalEventTest.allProjectsWarning'))
    return
  }

  router.push({
    path: '/PLC/detail',
    query: {
      id: projectId.value,
    },
  })
}
</script>

<template>
  <div>
    <FaPageHeader :title="pageTitle" @back="router.back()">
      <template #description>
        <div class="header-description">
          <span>{{ pageDesc }}</span>
        </div>
      </template>
      <div v-if="projectId" class="header-actions">
        <el-button plain @click="handleOpenProjectDetail">
          {{ $t('cylinderSignalEventTest.backToProject') }}
        </el-button>
      </div>
    </FaPageHeader>

    <FaPageMain>
      <CylinderSignalEventTester :project-id="projectId" />
    </FaPageMain>
  </div>
</template>

<style scoped>
.header-description {
  font-size: 13px;
  color: var(--el-text-color-secondary);
}

.header-actions {
  display: flex;
  gap: 12px;
}
</style>
