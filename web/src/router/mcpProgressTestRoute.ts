import type { RouteRecordRaw } from 'vue-router'

export const mcpProgressTestRoute: RouteRecordRaw = {
  path: '/mcp-progress-test',
  name: 'McpProgressTest',
  component: () => import('../views/mcp-progress-test/index.vue'),
  meta: {
    title: 'MCP 流式诊断测试',
    sidebar: false,
  },
}
