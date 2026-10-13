<script setup lang="ts">
const canvasBase64 = defineModel<string>({
  required: true,
  default: '',
})
const canvasBase64Raw = canvasBase64.value

const canvasRef = useTemplateRef<HTMLCanvasElement>('canvasRef')
const isDrawing = ref(false)
const ctx = ref<CanvasRenderingContext2D | null>()

// 历史记录相关
const history = ref<ImageData[]>([])
const currentStep = ref(-1)
const maxHistorySteps = 20 // 最大历史记录步数

// 颜色相关
const currentColor = ref('#ff0000')
const colorInputRef = ref<HTMLInputElement>()

// 线条粗细相关
const lineWidth = ref([10])

// 绘图模式相关
type DrawMode = 'brush' | 'line' | 'rectangle' | 'circle' | 'arrow'
const currentMode = ref<DrawMode>('brush')
const startPoint = ref<{ x: number, y: number } | null>(null)

// 初始化canvas
function initCanvas() {
  const canvas = canvasRef.value
  if (!canvas) {
    return
  }

  ctx.value = canvas.getContext('2d')
  if (!ctx.value) {
    return
  }

  // 设置画笔样式
  updateBrushStyle()

  // 加载图片到canvas
  const img = new Image()
  img.onload = () => {
    // 获取容器的实际显示尺寸
    const container = canvas.parentElement
    if (!container) {
      return
    }

    const containerWidth = container.clientWidth
    const aspectRatio = img.width / img.height

    // 计算canvas的显示尺寸，保持宽高比
    const displayWidth = containerWidth
    const displayHeight = containerWidth / aspectRatio

    // 设置canvas的CSS尺寸
    canvas.style.width = `${displayWidth}px`
    canvas.style.height = `${displayHeight}px`

    // 设置canvas的实际尺寸（用于绘制）
    canvas.width = img.width
    canvas.height = img.height

    // 绘制图片
    ctx.value?.drawImage(img, 0, 0)

    // 保存初始状态到历史记录
    saveToHistory()

    // 触发base64更新
    updateBase64()
  }
  img.src = canvasBase64.value
}

// 更新画笔样式
function updateBrushStyle() {
  if (ctx.value) {
    ctx.value.strokeStyle = currentColor.value
    ctx.value.lineWidth = lineWidth.value[0]
    ctx.value.lineCap = 'round'
    ctx.value.lineJoin = 'round'
  }
}

// 保存当前状态到历史记录
function saveToHistory() {
  const canvas = canvasRef.value
  if (!canvas || !ctx.value) {
    return
  }

  // 获取当前canvas的图像数据
  const imageData = ctx.value.getImageData(0, 0, canvas.width, canvas.height)

  // 如果当前不在历史记录的最后，删除后面的记录
  if (currentStep.value < history.value.length - 1) {
    history.value = history.value.slice(0, currentStep.value + 1)
  }

  // 添加新的状态
  history.value.push(imageData)
  currentStep.value++

  // 限制历史记录数量
  if (history.value.length > maxHistorySteps) {
    history.value.shift()
    currentStep.value--
  }
}

// 撤销功能
function undo() {
  if (currentStep.value > 0) {
    currentStep.value--
    restoreFromHistory()
  }
}

// 重做功能
function redo() {
  if (currentStep.value < history.value.length - 1) {
    currentStep.value++
    restoreFromHistory()
  }
}

// 从历史记录恢复状态
function restoreFromHistory() {
  const canvas = canvasRef.value
  if (!canvas || !ctx.value || currentStep.value < 0) {
    return
  }

  const imageData = history.value[currentStep.value]
  if (imageData) {
    ctx.value.putImageData(imageData, 0, 0)
    updateBase64()
  }
}

// 绘制箭头
function drawArrow(ctx: CanvasRenderingContext2D, start: { x: number, y: number }, end: { x: number, y: number }) {
  const headLength = 15 // 箭头头部长度
  const angle = Math.atan2(end.y - start.y, end.x - start.x)

  // 绘制箭头的线
  ctx.beginPath()
  ctx.moveTo(start.x, start.y)
  ctx.lineTo(end.x, end.y)
  ctx.stroke()

  // 绘制箭头头部
  ctx.beginPath()
  ctx.moveTo(end.x, end.y)
  ctx.lineTo(
    end.x - headLength * Math.cos(angle - Math.PI / 6),
    end.y - headLength * Math.sin(angle - Math.PI / 6),
  )
  ctx.moveTo(end.x, end.y)
  ctx.lineTo(
    end.x - headLength * Math.cos(angle + Math.PI / 6),
    end.y - headLength * Math.sin(angle + Math.PI / 6),
  )
  ctx.stroke()
}

// 获取鼠标在canvas上的坐标
function getMousePos(e: MouseEvent) {
  const canvas = canvasRef.value
  if (!canvas) {
    return { x: 0, y: 0 }
  }

  const rect = canvas.getBoundingClientRect()
  const scaleX = canvas.width / rect.width
  const scaleY = canvas.height / rect.height
  const x = (e.clientX - rect.left) * scaleX
  const y = (e.clientY - rect.top) * scaleY

  return { x, y }
}

// 切换绘图模式
function switchMode(mode: DrawMode) {
  currentMode.value = mode
}

// 开始绘制
function startDrawing(e: MouseEvent) {
  isDrawing.value = true
  const pos = getMousePos(e)
  startPoint.value = pos

  if (!ctx.value) {
    return
  }

  // 重新设置画笔样式
  updateBrushStyle()

  if (currentMode.value === 'brush') {
    ctx.value.beginPath()
    ctx.value.moveTo(pos.x, pos.y)
  }
}

// 绘制过程
function draw(e: MouseEvent) {
  if (!isDrawing.value || !ctx.value || !startPoint.value) {
    return
  }

  const pos = getMousePos(e)

  if (currentMode.value === 'brush') {
    // 画笔模式：自由绘制
    ctx.value.lineTo(pos.x, pos.y)
    ctx.value.stroke()
  }
  else {
    // 其他模式：实时预览
    // 先恢复到上一个状态
    restoreFromHistory()

    if (currentMode.value === 'line') {
      // 线条模式
      ctx.value.beginPath()
      ctx.value.moveTo(startPoint.value.x, startPoint.value.y)
      ctx.value.lineTo(pos.x, pos.y)
      ctx.value.stroke()
    }
    else if (currentMode.value === 'rectangle') {
      // 矩形模式
      const width = pos.x - startPoint.value.x
      const height = pos.y - startPoint.value.y
      ctx.value.strokeRect(startPoint.value.x, startPoint.value.y, width, height)
    }
    else if (currentMode.value === 'circle') {
      // 圆形模式
      const radius = Math.sqrt(
        (pos.x - startPoint.value.x) ** 2
        + (pos.y - startPoint.value.y) ** 2,
      )
      ctx.value.beginPath()
      ctx.value.arc(startPoint.value.x, startPoint.value.y, radius, 0, 2 * Math.PI)
      ctx.value.stroke()
    }
    else if (currentMode.value === 'arrow') {
      // 箭头模式
      drawArrow(ctx.value, startPoint.value, pos)
    }
  }

  // 更新base64
  updateBase64()
}

// 结束绘制
function stopDrawing() {
  if (isDrawing.value && startPoint.value) {
    isDrawing.value = false
    startPoint.value = null
    // 绘制结束后保存到历史记录
    saveToHistory()
  }
}

// 清除涂鸦
function clearCanvas() {
  const canvas = canvasRef.value
  if (!canvas || !ctx.value) {
    return
  }

  // 重新绘制原始图片
  const img = new Image()
  img.onload = () => {
    ctx.value?.clearRect(0, 0, canvas.width, canvas.height)
    ctx.value?.drawImage(img, 0, 0)

    // 重新设置画笔样式
    updateBrushStyle()

    // 清除历史记录
    history.value = []
    currentStep.value = -1

    // 保存清除后的状态到历史记录，这样第一次绘制时就有状态可以恢复
    saveToHistory()
  }
  img.src = canvasBase64Raw

  // 触发base64更新
  canvasBase64.value = canvasBase64Raw
}

// 删除canvas
function removeCanvas() {
  canvasBase64.value = ''
}

// 将canvas转换为base64并触发更新
function updateBase64() {
  canvasBase64.value = canvasRef.value?.toDataURL('image/png') ?? ''
}

// 更新画笔颜色
function updateBrushColor(color: string) {
  currentColor.value = color
  updateBrushStyle()
}

// 打开颜色选择器
function openColorPicker() {
  colorInputRef.value?.click()
}

// 处理颜色变化
function handleColorChange(event: Event) {
  const target = event.target as HTMLInputElement
  if (target.value) {
    updateBrushColor(target.value)
  }
}

// 监听线条粗细变化
watch(lineWidth, () => {
  updateBrushStyle()
})

// 计算按钮状态
const canUndo = computed(() => currentStep.value > 0)
const canRedo = computed(() => currentStep.value < history.value.length - 1)

onMounted(() => {
  nextTick(() => {
    initCanvas()
  })
})
</script>

<template>
  <div class="flex flex-col gap-2">
    <div class="flex gap-2">
      <FaButton variant="outline" size="icon" @click="clearCanvas">
        <FaIcon name="i-lucide:eraser" class="size-4" />
      </FaButton>
      <FaButtonGroup class="gap-0 space-x-[-1px]">
        <FaButton variant="outline" size="icon" :disabled="!canUndo" @click="undo">
          <FaIcon name="i-lucide:undo" class="size-4" />
        </FaButton>
        <FaButton variant="outline" size="icon" :disabled="!canRedo" @click="redo">
          <FaIcon name="i-lucide:redo" class="size-4" />
        </FaButton>
      </FaButtonGroup>
      <FaButtonGroup class="gap-0 space-x-[-1px]">
        <FaButton variant="outline" size="icon" @click="openColorPicker">
          <input ref="colorInputRef" type="color" :value="currentColor" class="pointer-events-none size-full bg-transparent p-2" @change="handleColorChange">
        </FaButton>
        <FaPopover class="min-w-40">
          <FaButton variant="outline" size="icon">
            <FaIcon name="i-fluent:line-thickness-24-filled" class="size-4" />
          </FaButton>
          <template #panel>
            <FaSlider v-model="lineWidth" :min="2" :max="18" :step="4" :tooltip="false" />
          </template>
        </FaPopover>
      </FaButtonGroup>
      <FaButtonGroup class="gap-0 space-x-[-1px]">
        <FaButton :variant="currentMode === 'brush' ? 'default' : 'outline'" size="icon" :class="{ 'z-1': currentMode === 'brush' }" @click="switchMode('brush')">
          <FaIcon name="i-streamline-ultimate:pen-draw-1-bold" class="size-4" />
        </FaButton>
        <FaButton :variant="currentMode === 'line' ? 'default' : 'outline'" size="icon" :class="{ 'z-1': currentMode === 'line' }" @click="switchMode('line')">
          <FaIcon name="i-lucide:slash" class="size-4" />
        </FaButton>
        <FaButton :variant="currentMode === 'rectangle' ? 'default' : 'outline'" size="icon" :class="{ 'z-1': currentMode === 'rectangle' }" @click="switchMode('rectangle')">
          <FaIcon name="i-lucide:square" class="size-4" />
        </FaButton>
        <FaButton :variant="currentMode === 'circle' ? 'default' : 'outline'" size="icon" :class="{ 'z-1': currentMode === 'circle' }" @click="switchMode('circle')">
          <FaIcon name="i-lucide:circle" class="size-4" />
        </FaButton>
        <FaButton :variant="currentMode === 'arrow' ? 'default' : 'outline'" size="icon" :class="{ 'z-1': currentMode === 'arrow' }" @click="switchMode('arrow')">
          <FaIcon name="i-lucide:arrow-right" class="size-4" />
        </FaButton>
      </FaButtonGroup>
      <FaButton variant="ghost" size="icon" class="ms-auto" @click="removeCanvas">
        <FaIcon name="i-lucide:trash" class="size-4" />
      </FaButton>
    </div>
    <!-- 隐藏的原生颜色选择器 -->
    <canvas
      ref="canvasRef"
      class="block w-full cursor-crosshair border rounded-lg"
      @mousedown="startDrawing"
      @mousemove="draw"
      @mouseup="stopDrawing"
      @mouseleave="stopDrawing"
    />
  </div>
</template>
