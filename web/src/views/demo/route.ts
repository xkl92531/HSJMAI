// 工作流测试路由配置
export default {
  path: '/demo/workflow-test',
  name: 'WorkflowTest',
  component: () => import('@/views/demo/WorkflowTest.vue'),
  meta: {
    title: '多智能体工作流测试',
    icon: 'Grid',
    noCache: true,
  },
}
