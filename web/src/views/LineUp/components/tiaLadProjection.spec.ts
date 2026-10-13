import type { LineUpConditionRuntimeValue } from '@/api/modules/LineUp/monitor'
import type { LineUpGraphLogicNetwork, LineUpGraphLogicPart } from '@/api/modules/LineUp/types'
import { describe, expect, it } from 'vitest'
import {
  expandLadOrGates,
  isLadComparatorKind,
  layoutLadNetwork,
  projectLadGeometry,
  projectLadNetwork,
  tiaLadComparisonExpression,
  tiaLadNetworkExpression,
  tiaLadPartGlyph,
  tiaLadSymbolHeight,
  wrapLadLabelLines,
} from './tiaLadProjection'

function part(
  sourceKey: string,
  uId: number,
  partName: string,
  overrides: Partial<LineUpGraphLogicPart> = {},
): LineUpGraphLogicPart {
  return {
    sourceKey,
    uId,
    partName,
    scope: '',
    symbolPath: [],
    constantValue: '',
    negated: false,
    attributes: [],
    templateValues: [],
    pinNames: [],
    ...overrides,
  }
}

function network(parts: LineUpGraphLogicPart[], wires?: LineUpGraphLogicNetwork['wires']): LineUpGraphLogicNetwork {
  return {
    sourceKey: 'network:transition:2:condition:1',
    ownerSourceKey: 'transition:2',
    role: 'TransitionCondition',
    ordinal: 1,
    programmingLanguage: 'LAD',
    title: 'Transition condition',
    parts,
    wires: wires ?? [
      {
        sourceKey: 'wire:1',
        uId: 101,
        endpoints: [
          { kind: 'Powerrail', uId: null, name: '' },
          { kind: 'IdentCon', uId: 11, name: 'in' },
        ],
      },
      {
        sourceKey: 'wire:2',
        uId: 102,
        endpoints: [
          { kind: 'NameCon', uId: 11, name: 'out' },
          { kind: 'IdentCon', uId: 12, name: 'in1' },
          { kind: 'IdentCon', uId: 12, name: 'in2' },
        ],
      },
      {
        sourceKey: 'wire:3',
        uId: 103,
        endpoints: [
          { kind: 'NameCon', uId: 12, name: 'out' },
          { kind: 'IdentCon', uId: 13, name: 'in' },
        ],
      },
    ],
  }
}

function runtime(
  partSourceKey: string,
  state: LineUpConditionRuntimeValue['state'],
  quality: LineUpConditionRuntimeValue['quality'],
): LineUpConditionRuntimeValue {
  return {
    canonicalKey: `runtime:${partSourceKey}`,
    ownerSourceKey: 'transition:2',
    networkSourceKey: 'network:transition:2:condition:1',
    partSourceKey,
    state,
    quality,
    value: state === 'True',
    sourceTimestamp: null,
    qualityCode: '',
  }
}

describe('tIA LAD part glyph', () => {
  it('draws contacts without text and maps boxes/coils to TIA operator glyphs', () => {
    const source = network([
      part('part:contact', 11, 'Contact', { symbolPath: ['DB1', 'Ready'], pinNames: ['in', 'operand', 'out'] }),
      part('part:negated-contact', 12, 'Contact', { negated: true, symbolPath: ['DB1', 'Stop'], pinNames: ['in', 'operand', 'out'] }),
      part('part:ge', 13, 'Ge', { constantValue: 'T#5S', pinNames: ['in1', 'in2', 'out'] }),
      part('part:not', 14, 'Not', { pinNames: ['in', 'out'] }),
      part('part:or', 15, 'O', { pinNames: ['in1', 'in2', 'out'] }),
      part('part:coil', 16, 'TrCoil', { symbolPath: ['DB1', 'Coil'], pinNames: ['in'] }),
      part('part:mystery', 17, 'StrangeBox', { pinNames: ['in', 'out'] }),
    ])

    const projection = projectLadNetwork(source, [])
    const glyphByPart = new Map(projection.symbols.map(item => [item.partName, tiaLadPartGlyph(item)]))

    expect(glyphByPart.get('Contact')).toBeNull()
    expect(glyphByPart.get('Ge')).toBe('>=')
    expect(glyphByPart.get('Not')).toBe('NOT')
    // 博图 FBD：OR 盒写 >=1（IEC 61131）；线圈是普通 ()，内部不写字
    expect(glyphByPart.get('O')).toBe('>=1')
    expect(glyphByPart.get('TrCoil')).toBeNull()
    expect(glyphByPart.get('StrangeBox')).toBe('StrangeBox')
  })
})

describe('tIA LAD network projection', () => {
  it('retains source order, constants, pins, and ordered wire connectivity', () => {
    const source = network([
      part('part:contact', 11, 'Contact', {
        symbolPath: ['DB_Line', 'Ready'],
        attributes: [{ name: 'Operand', value: 'Ready' }],
        templateValues: [{ name: 'Type', value: 'Bool' }],
        pinNames: ['in', 'out'],
      }),
      part('part:eq', 12, 'Eq', { constantValue: '7', pinNames: ['in1', 'in2', 'out'] }),
      part('part:coil', 13, 'TrCoil', { symbolPath: ['DB_Line', 'Permit'], pinNames: ['in'] }),
    ])

    const projection = projectLadNetwork(source, [runtime('part:contact', 'True', 'Good')])

    expect(projection.symbols.map(item => item.key)).toEqual(['part:contact', 'part:eq', 'part:coil'])
    expect(projection.symbols.map(item => item.uId)).toEqual([11, 12, 13])
    expect(projection.symbols[0]).toMatchObject({
      kind: 'contact',
      label: 'DB_Line.Ready',
      symbolPath: ['DB_Line', 'Ready'],
      attributes: [{ name: 'Operand', value: 'Ready' }],
      templateValues: [{ name: 'Type', value: 'Bool' }],
      pins: ['in', 'out'],
      runtime: { state: 'true', quality: 'Good' },
    })
    expect(projection.symbols[1]).toMatchObject({
      kind: 'eq',
      constantValue: '7',
      pins: ['in1', 'in2', 'out'],
    })
    expect(projection.wires).toEqual([
      {
        key: 'wire:1',
        uId: 101,
        endpoints: [
          { kind: 'Powerrail', uId: null, name: '' },
          { kind: 'IdentCon', uId: 11, name: 'in' },
        ],
      },
      {
        key: 'wire:2',
        uId: 102,
        endpoints: [
          { kind: 'NameCon', uId: 11, name: 'out' },
          { kind: 'IdentCon', uId: 12, name: 'in1' },
          { kind: 'IdentCon', uId: 12, name: 'in2' },
        ],
      },
      {
        key: 'wire:3',
        uId: 103,
        endpoints: [
          { kind: 'NameCon', uId: 12, name: 'out' },
          { kind: 'IdentCon', uId: 13, name: 'in' },
        ],
      },
    ])

    projection.symbols[0].attributes[0].value = 'ProjectionOnly'
    projection.symbols[0].templateValues[0].value = 'ProjectionOnly'
    expect(source.parts[0].attributes).toEqual([{ name: 'Operand', value: 'Ready' }])
    expect(source.parts[0].templateValues).toEqual([{ name: 'Type', value: 'Bool' }])

    source.parts[0].attributes[0].value = 'SourceOnly'
    source.parts[0].templateValues[0].value = 'SourceOnly'
    expect(projection.symbols[0].attributes).toEqual([{ name: 'Operand', value: 'ProjectionOnly' }])
    expect(projection.symbols[0].templateValues).toEqual([{ name: 'Type', value: 'ProjectionOnly' }])
  })

  it('projects every supported fixture part and preserves unknown extensions', () => {
    const partNames = ['Contact', 'Eq', 'Ge', 'Gt', 'Le', 'Lt', 'Not', 'O', 'IlCoil', 'SvCoil', 'TrCoil', 'VendorExtension']
    const projection = projectLadNetwork(network(partNames.map((name, index) =>
      part(`part:${index + 1}`, index + 1, name),
    )), [])

    expect(projection.symbols.map(item => item.kind)).toEqual([
      'contact',
      'eq',
      'ge',
      'gt',
      'le',
      'lt',
      'not',
      'or',
      'il-coil',
      'sv-coil',
      'tr-coil',
      'unknown',
    ])
    expect(projection.symbols.at(-1)).toMatchObject({
      key: 'part:12',
      kind: 'unknown',
      label: 'VendorExtension',
    })
  })

  it('does not present missing, unknown, stale, bad, or ambiguous runtime data as true', () => {
    const source = network([
      part('part:missing', 21, 'Contact'),
      part('part:unknown', 22, 'Contact'),
      part('part:stale', 23, 'Contact'),
      part('part:bad', 24, 'Contact'),
      part('part:ambiguous', 25, 'Contact'),
    ])
    const projection = projectLadNetwork(source, [
      runtime('part:unknown', 'Unknown', 'Good'),
      runtime('part:stale', 'True', 'Stale'),
      runtime('part:bad', 'True', 'Bad'),
      runtime('part:ambiguous', 'True', 'Good'),
      { ...runtime('part:ambiguous', 'True', 'Good'), canonicalKey: 'runtime:duplicate' },
    ])

    expect(projection.symbols.map(item => item.runtime.state)).toEqual([
      'unknown',
      'unknown',
      'unknown',
      'unknown',
      'unknown',
    ])
    expect(projection.symbols.map(item => item.runtime.quality)).toEqual([
      'Unknown',
      'Good',
      'Stale',
      'Bad',
      'Unknown',
    ])
  })

  it('uses only the unique exact owner-network-part value for one LAD part', () => {
    const source = network([part('part:contact', 21, 'Contact')])
    const exact = runtime('part:contact', 'True', 'Good')
    const foreignOwner = { ...exact, canonicalKey: 'runtime:foreign-owner', ownerSourceKey: 'transition:20' }
    const foreignNetwork = { ...exact, canonicalKey: 'runtime:foreign-network', networkSourceKey: 'network:other' }

    expect(projectLadNetwork(source, [foreignOwner, exact, foreignNetwork]).symbols[0].runtime.state)
      .toBe('true')
  })

  it('attaches FB260 Access values to Contact and comparator pins without rendering Access blocks', () => {
    const source = network([
      part('part:access:ack', 21, 'Access', { scope: 'LocalVariable', symbolPath: ['ACK_EF'] }),
      part('part:access:timer', 22, 'Access', { scope: 'LocalVariable', symbolPath: ['判断滑台1状态', 'U'] }),
      part('part:access:constant', 23, 'Access', { scope: 'TypedConstant', constantValue: 'T#5S' }),
      part('part:contact', 24, 'Contact', { pinNames: ['in', 'operand', 'out'] }),
      part('part:gt', 25, 'Gt', { pinNames: ['in1', 'in2', 'out', 'pre'] }),
      part('part:coil', 26, 'SvCoil', { pinNames: ['in'] }),
    ])
    source.wires = [
      { sourceKey: 'wire:27', uId: 27, endpoints: [{ kind: 'Powerrail', uId: null, name: '' }, { kind: 'NameCon', uId: 24, name: 'in' }] },
      { sourceKey: 'wire:28', uId: 28, endpoints: [{ kind: 'IdentCon', uId: 21, name: '' }, { kind: 'NameCon', uId: 24, name: 'operand' }] },
      { sourceKey: 'wire:29', uId: 29, endpoints: [{ kind: 'NameCon', uId: 24, name: 'out' }, { kind: 'NameCon', uId: 25, name: 'pre' }] },
      { sourceKey: 'wire:30', uId: 30, endpoints: [{ kind: 'IdentCon', uId: 22, name: '' }, { kind: 'NameCon', uId: 25, name: 'in1' }] },
      { sourceKey: 'wire:31', uId: 31, endpoints: [{ kind: 'IdentCon', uId: 23, name: '' }, { kind: 'NameCon', uId: 25, name: 'in2' }] },
      { sourceKey: 'wire:32', uId: 32, endpoints: [{ kind: 'NameCon', uId: 25, name: 'out' }, { kind: 'NameCon', uId: 26, name: 'in' }] },
    ]

    const rawOperand = {
      ...runtime('part:access:ack', 'False', 'Good'),
      value: '17',
      qualityCode: 'RawOperandCode',
      sourceTimestamp: '2026-08-26T00:00:00.0000000Z',
    }
    const derivedContact = {
      ...runtime('part:contact', 'True', 'Good'),
      value: true,
      qualityCode: 'DerivedPartCode',
      sourceTimestamp: '2026-08-26T00:00:01.0000000Z',
    }
    const projection = projectLadNetwork(source, [rawOperand, derivedContact])

    expect(projection.symbols.map(item => item.key)).toEqual(['part:contact', 'part:gt', 'part:coil'])
    expect(projection.symbols[0]).toMatchObject({
      kind: 'contact',
      label: 'ACK_EF',
      operands: [{
        key: 'part:access:ack',
        targetPin: 'operand',
        label: 'ACK_EF',
        scope: 'LocalVariable',
        symbolPath: ['ACK_EF'],
        constantValue: '',
        runtime: {
          state: 'false',
          quality: 'Good',
          value: '17',
          qualityCode: 'RawOperandCode',
          sourceTimestamp: '2026-08-26T00:00:00.0000000Z',
        },
      }],
    })
    expect(projection.symbols[0].runtime).toEqual({
      state: 'true',
      quality: 'Good',
      value: true,
      qualityCode: 'DerivedPartCode',
      sourceTimestamp: '2026-08-26T00:00:01.0000000Z',
    })
    expect(projection.symbols[1].operands).toMatchObject([
      { key: 'part:access:timer', targetPin: 'in1', label: '判断滑台1状态.U' },
      { key: 'part:access:constant', targetPin: 'in2', label: 'T#5S' },
    ])
    expect(projection.wires.map(item => item.key)).toEqual(['wire:27', 'wire:29', 'wire:32'])
    expect(projection.accessFallbacks).toEqual([])

    const geometry = projectLadGeometry(projection, layoutLadNetwork(projection))
    expect(geometry.wires.map(item => item.key)).toEqual(['wire:27', 'wire:29', 'wire:32', 'rail:part:coil'])
    expect(geometry.markers).toEqual([])
    // 监控态：触点标签上方还要叠 判断行+值状态行（3 行共 44px 向上），
    // 首行顶边随之上移到 52 → 母线进线 y = 52 + 26 = 78（不再探出画布顶端）
    expect(geometry.wires[0].points).toBe('28,78 78,78')
  })

  it('keeps an unattached Access as an explicit fallback instead of a gray unknown block', () => {
    const source = network([
      part('part:access:orphan', 21, 'Access', { scope: 'GlobalVariable', symbolPath: ['DB_Line', 'Orphan'] }),
      part('part:contact', 24, 'Contact', { pinNames: ['in', 'out'] }),
    ])
    source.wires = [{
      sourceKey: 'wire:physical',
      uId: 27,
      endpoints: [{ kind: 'Powerrail', uId: null, name: '' }, { kind: 'NameCon', uId: 24, name: 'in' }],
    }]

    const projection = projectLadNetwork(source, [])

    expect(projection.symbols.map(item => item.key)).toEqual(['part:contact'])
    expect(projection.accessFallbacks).toMatchObject([{
      key: 'part:access:orphan',
      label: 'DB_Line.Orphan',
      scope: 'GlobalVariable',
      targetPin: '',
    }])
  })
})

describe('tIA LAD ladder layout', () => {
  it('lays a series transition chain left-to-right like TIA Portal', () => {
    // 博图 GRAPH 转换条件典型形态：母线 → 常开 → 常开 → 常闭 → TrCoil
    const source = network([
      part('part:cond1', 24, 'Contact', { pinNames: ['in', 'operand', 'out'] }),
      part('part:cond2', 25, 'Contact', { pinNames: ['in', 'operand', 'out'] }),
      part('part:cond3', 26, 'Contact', { pinNames: ['in', 'operand', 'out'], negated: true }),
      part('part:coil', 27, 'TrCoil', { pinNames: ['in'] }),
    ], [
      { sourceKey: 'wire:28', uId: 28, endpoints: [{ kind: 'Powerrail', uId: null, name: '' }, { kind: 'NameCon', uId: 24, name: 'in' }] },
      { sourceKey: 'wire:30', uId: 30, endpoints: [{ kind: 'NameCon', uId: 24, name: 'out' }, { kind: 'NameCon', uId: 25, name: 'in' }] },
      { sourceKey: 'wire:32', uId: 32, endpoints: [{ kind: 'NameCon', uId: 25, name: 'out' }, { kind: 'NameCon', uId: 26, name: 'in' }] },
      { sourceKey: 'wire:34', uId: 34, endpoints: [{ kind: 'NameCon', uId: 26, name: 'out' }, { kind: 'NameCon', uId: 27, name: 'in' }] },
    ])

    const projection = projectLadNetwork(source, [])
    const layout = layoutLadNetwork(projection)

    expect(layout.columnCount).toBe(4)
    expect(layout.rowCount).toBe(1)
    expect(layout.positions.get('part:cond1')).toEqual({ x: 78, y: 40 })
    expect(layout.positions.get('part:cond2')).toEqual({ x: 232, y: 40 })
    expect(layout.positions.get('part:cond3')).toEqual({ x: 386, y: 40 })
    expect(layout.positions.get('part:coil')).toEqual({ x: 540, y: 40 })

    const geometry = projectLadGeometry(projection, layout)
    expect(geometry.hasPowerrail).toBe(true)
    // 博图：梯级始于左母线、终于右母线；线圈是逻辑完结点不画本体，
    // 合流线从线圈进线点直达右母线
    expect(geometry.rightRailX).toBe(778)
    expect(geometry.wires.map(wire => wire.points)).toEqual([
      '28,66 78,66',
      '190,66 232,66',
      '344,66 386,66',
      // 触点与线圈同一水平中轴：末段导线拉直直插线圈（博图串联支路形态）
      '498,66 540,66',
      '540,66 778,66',
    ])
    // 离线（无监控数据，导入预览）：全部导线保持黑色
    expect(geometry.wires.every(wire => wire.state === null)).toBe(true)
  })

  it('stacks parallel branches vertically and routes merge wires orthogonally', () => {
    // 母线 → 两条并联支路（OR）→ 汇合进 TrCoil
    const source = network([
      part('part:branch-a', 31, 'Contact', { pinNames: ['in', 'out'] }),
      part('part:branch-b', 32, 'Contact', { pinNames: ['in', 'out'] }),
      part('part:coil', 33, 'TrCoil', { pinNames: ['in'] }),
    ], [
      { sourceKey: 'wire:41', uId: 41, endpoints: [{ kind: 'Powerrail', uId: null, name: '' }, { kind: 'NameCon', uId: 31, name: 'in' }] },
      { sourceKey: 'wire:42', uId: 42, endpoints: [{ kind: 'Powerrail', uId: null, name: '' }, { kind: 'NameCon', uId: 32, name: 'in' }] },
      { sourceKey: 'wire:43', uId: 43, endpoints: [{ kind: 'NameCon', uId: 31, name: 'out' }, { kind: 'NameCon', uId: 33, name: 'in' }] },
      { sourceKey: 'wire:44', uId: 44, endpoints: [{ kind: 'NameCon', uId: 32, name: 'out' }, { kind: 'NameCon', uId: 33, name: 'in' }] },
    ])

    const projection = projectLadNetwork(source, [])
    const layout = layoutLadNetwork(projection)

    expect(layout.columnCount).toBe(2)
    expect(layout.rowCount).toBe(2)
    expect(layout.positions.get('part:branch-a')).toEqual({ x: 78, y: 40 })
    expect(layout.positions.get('part:branch-b')).toEqual({ x: 78, y: 136 })
    // 单元素列整体居中：线圈被放到两行中间
    expect(layout.positions.get('part:coil')).toEqual({ x: 232, y: 88 })

    const geometry = projectLadGeometry(projection, layout)
    const wireBySource = new Map(geometry.wires.map(wire => [wire.key, wire.points]))
    expect(wireBySource.get('wire:42')).toBe('28,162 78,162')
    // 上支路汇合：横 → 竖 → 横 的正交折线（线圈从中轴 114 进线）
    expect(wireBySource.get('wire:43')).toBe('190,66 214,66 214,114 232,114')
    expect(wireBySource.get('wire:44')).toBe('190,162 214,162 214,114 232,114')
    // 居中完结点：合流线直达右母线（线圈不画本体）
    expect(wireBySource.get('rail:part:coil')).toBe('232,114 470,114')
  })

  it('returns an empty layout for a network without functional parts', () => {
    const layout = layoutLadNetwork(projectLadNetwork(network([]), []))
    expect(layout.positions.size).toBe(0)
    expect(layout.columnCount).toBe(0)
    expect(layout.width).toBe(360)
  })
})

describe('tIA LAD endpoint geometry', () => {
  it('includes the powerrail only when a source wire has a Powerrail endpoint', () => {
    const withPowerrail = projectLadGeometry(projectLadNetwork(network([
      part('part:contact', 11, 'Contact', { pinNames: ['in'] }),
    ]), []), layoutLadNetwork(projectLadNetwork(network([
      part('part:contact', 11, 'Contact', { pinNames: ['in'] }),
    ]), [])))
    const withoutPowerrailSource = network([
      part('part:contact', 11, 'Contact', { pinNames: ['in'] }),
    ])
    withoutPowerrailSource.wires = [{
      sourceKey: 'wire:no-rail',
      uId: 201,
      endpoints: [{ kind: 'IdentCon', uId: 11, name: 'in' }],
    }]

    expect(withPowerrail.hasPowerrail).toBe(true)
    expect(projectLadGeometry(
      projectLadNetwork(withoutPowerrailSource, []),
      layoutLadNetwork(projectLadNetwork(withoutPowerrailSource, [])),
    ).hasPowerrail).toBe(false)
  })

  it('projects OpenCon as an explicit open marker beside its connected endpoint', () => {
    const source = network([
      part('part:contact', 11, 'Contact', { pinNames: ['in'] }),
    ])
    source.wires = [{
      sourceKey: 'wire:open',
      uId: 202,
      endpoints: [
        { kind: 'OpenCon', uId: null, name: '' },
        { kind: 'IdentCon', uId: 11, name: 'in' },
      ],
    }]

    const projection = projectLadNetwork(source, [])
    const geometry = projectLadGeometry(projection, layoutLadNetwork(projection))

    expect(geometry.wires[0].points).toBe('48,66 78,66')
    expect(geometry.markers).toEqual([{
      key: 'wire:open:0',
      kind: 'open',
      x: 48,
      y: 66,
      label: 'OpenCon',
    }])
  })

  it('offsets multiple dangling markers on the same wire by stable endpoint order', () => {
    const source = network([
      part('part:contact', 11, 'Contact', { pinNames: ['in'] }),
    ])
    source.wires = [{
      sourceKey: 'wire:double-open',
      uId: 204,
      endpoints: [
        { kind: 'OpenCon', uId: null, name: '' },
        { kind: 'OpenCon', uId: null, name: '' },
        { kind: 'IdentCon', uId: 11, name: 'in' },
      ],
    }]

    const projection = projectLadNetwork(source, [])
    const geometry = projectLadGeometry(projection, layoutLadNetwork(projection))

    expect(geometry.markers).toEqual([
      { key: 'wire:double-open:0', kind: 'open', x: 48, y: 60, label: 'OpenCon' },
      { key: 'wire:double-open:1', kind: 'open', x: 48, y: 72, label: 'OpenCon' },
    ])
    expect(new Set(geometry.markers.map(marker => `${marker.x},${marker.y}`)).size).toBe(2)
  })

  it('projects an endpoint with an unknown Part UID as a visible dangling marker', () => {
    const source = network([
      part('part:contact', 11, 'Contact', { pinNames: ['in'] }),
    ])
    source.wires = [{
      sourceKey: 'wire:unknown',
      uId: 203,
      endpoints: [
        { kind: 'IdentCon', uId: 999, name: 'in' },
        { kind: 'IdentCon', uId: 11, name: 'in' },
      ],
    }]

    const projection = projectLadNetwork(source, [])
    const geometry = projectLadGeometry(projection, layoutLadNetwork(projection))

    expect(geometry.markers).toEqual([{
      key: 'wire:unknown:0',
      kind: 'unknown',
      x: 48,
      y: 66,
      label: 'IdentCon · UID 999',
    }])
  })
})

describe('tIA LAD or-gate expansion', () => {
  it('expands an Or box into parallel branches that merge into the coil', () => {
    // 博图 LAD 视图：Or(>=1) 盒不画盒，前后条件上下并联、汇合进完结节点（线圈）
    const source = network([
      part('part:a', 21, 'Contact', { symbolPath: ['A'], pinNames: ['in', 'operand', 'out'] }),
      part('part:b', 22, 'Contact', { symbolPath: ['B'], pinNames: ['in', 'operand', 'out'] }),
      part('part:or', 23, 'O', { pinNames: ['in1', 'in2', 'out'] }),
      part('part:coil', 24, 'TrCoil', { symbolPath: ['D'], pinNames: ['in'] }),
    ], [
      { sourceKey: 'w:1', uId: 31, endpoints: [{ kind: 'Powerrail', uId: null, name: '' }, { kind: 'NameCon', uId: 21, name: 'in' }] },
      { sourceKey: 'w:2', uId: 32, endpoints: [{ kind: 'Powerrail', uId: null, name: '' }, { kind: 'NameCon', uId: 22, name: 'in' }] },
      { sourceKey: 'w:3', uId: 33, endpoints: [{ kind: 'NameCon', uId: 21, name: 'out' }, { kind: 'NameCon', uId: 23, name: 'in1' }] },
      { sourceKey: 'w:4', uId: 34, endpoints: [{ kind: 'NameCon', uId: 22, name: 'out' }, { kind: 'NameCon', uId: 23, name: 'in2' }] },
      { sourceKey: 'w:5', uId: 35, endpoints: [{ kind: 'NameCon', uId: 23, name: 'out' }, { kind: 'NameCon', uId: 24, name: 'in' }] },
    ])

    const projection = projectLadNetwork(source, [])
    const expanded = expandLadOrGates(projection)

    expect(expanded.symbols.map(symbol => symbol.kind)).toEqual(['contact', 'contact', 'tr-coil'])
    // 两条并联支路都直接汇入线圈 in（与 Map 顺序无关地断言馈线集合）
    const coilFeeders = expanded.wires
      .filter(wire => wire.endpoints[1].uId === 24 && wire.endpoints[1].name === 'in')
      .map(wire => wire.endpoints[0].uId)
      .sort((a, b) => (a ?? 0) - (b ?? 0))
    expect(coilFeeders).toEqual([21, 22])

    const layout = layoutLadNetwork(expanded)
    expect(layout.columnCount).toBe(2)
    const geometry = projectLadGeometry(expanded, layout)
    const mergeWires = geometry.wires.filter(wire => wire.key.startsWith('or:'))
    expect(mergeWires.map(wire => wire.points)).toEqual([
      '190,66 214,66 214,114 232,114',
      '190,162 214,162 214,114 232,114',
    ])
    // 完结点垂直居中：合流后一条线直达右母线（线圈不画本体）
    expect(layout.positions.get('part:coil')).toEqual({ x: 232, y: 88 })
    expect(geometry.wires.at(-1)?.points).toBe('232,114 470,114')
  })

  it('collapses cascaded Or boxes into one parallel merge', () => {
    const source = network([
      part('part:a', 21, 'Contact', { symbolPath: ['A'], pinNames: ['in', 'operand', 'out'] }),
      part('part:b', 22, 'Contact', { symbolPath: ['B'], pinNames: ['in', 'operand', 'out'] }),
      part('part:or1', 23, 'O', { pinNames: ['in1', 'in2', 'out'] }),
      part('part:or2', 24, 'O', { pinNames: ['in1', 'in2', 'out'] }),
      part('part:coil', 25, 'TrCoil', { symbolPath: ['D'], pinNames: ['in'] }),
    ], [
      { sourceKey: 'c:1', uId: 41, endpoints: [{ kind: 'Powerrail', uId: null, name: '' }, { kind: 'NameCon', uId: 21, name: 'in' }] },
      { sourceKey: 'c:2', uId: 42, endpoints: [{ kind: 'Powerrail', uId: null, name: '' }, { kind: 'NameCon', uId: 22, name: 'in' }] },
      { sourceKey: 'c:3', uId: 43, endpoints: [{ kind: 'NameCon', uId: 21, name: 'out' }, { kind: 'NameCon', uId: 23, name: 'in1' }] },
      { sourceKey: 'c:4', uId: 44, endpoints: [{ kind: 'NameCon', uId: 23, name: 'out' }, { kind: 'NameCon', uId: 24, name: 'in1' }] },
      { sourceKey: 'c:5', uId: 45, endpoints: [{ kind: 'NameCon', uId: 22, name: 'out' }, { kind: 'NameCon', uId: 24, name: 'in2' }] },
      { sourceKey: 'c:6', uId: 46, endpoints: [{ kind: 'NameCon', uId: 24, name: 'out' }, { kind: 'NameCon', uId: 25, name: 'in' }] },
    ])

    const expanded = expandLadOrGates(projectLadNetwork(source, []))

    expect(expanded.symbols.map(symbol => symbol.kind)).toEqual(['contact', 'contact', 'tr-coil'])
    expect(expanded.wires.filter(wire => wire.endpoints[1].uId === 25)).toHaveLength(2)
    expect(new Set(expanded.wires.filter(wire => wire.endpoints[1].uId === 25).map(wire => wire.endpoints[0].uId))).toEqual(new Set([21, 22]))
  })

  it('leaves networks without Or boxes untouched', () => {
    const projection = projectLadNetwork(network([
      part('part:contact', 11, 'Contact', { pinNames: ['in'] }),
    ]), [])

    expect(expandLadOrGates(projection)).toBe(projection)
  })
})

describe('tIA LAD comparator expression', () => {
  it('builds the comparison expression ordered by in1/in2 pins', () => {
    // 比较器不是盒：就是一个表达式（左 操作符 右）
    const source = network([
      part('access:left', 31, 'Access', { symbolPath: ['RT_DATA', 'MOP', 'AUTO'] }),
      part('access:right', 32, 'Access', { constantValue: '5' }),
      part('part:gt', 25, 'Gt', { pinNames: ['in1', 'in2', 'out'] }),
      part('part:eq', 26, 'Eq', { pinNames: ['in1', 'in2', 'out'] }),
    ], [
      { sourceKey: 'w:l', uId: 61, endpoints: [{ kind: 'IdentCon', uId: 31, name: '' }, { kind: 'NameCon', uId: 25, name: 'in1' }] },
      { sourceKey: 'w:r', uId: 62, endpoints: [{ kind: 'IdentCon', uId: 32, name: '' }, { kind: 'NameCon', uId: 25, name: 'in2' }] },
      { sourceKey: 'w:e', uId: 63, endpoints: [{ kind: 'IdentCon', uId: 31, name: '' }, { kind: 'NameCon', uId: 26, name: 'in2' }] },
      { sourceKey: 'w:e2', uId: 64, endpoints: [{ kind: 'IdentCon', uId: 32, name: '' }, { kind: 'NameCon', uId: 26, name: 'in1' }] },
    ])

    const symbols = projectLadNetwork(source, []).symbols
    const gt = symbols.find(symbol => symbol.kind === 'gt')!
    const eq = symbols.find(symbol => symbol.kind === 'eq')!

    expect(isLadComparatorKind('gt')).toBe(true)
    expect(isLadComparatorKind('contact')).toBe(false)
    expect(tiaLadComparisonExpression(gt)).toBe('RT_DATA.MOP.AUTO > 5')
    // in2/in1 喂入顺序不影响表达式按引脚排序
    expect(tiaLadComparisonExpression(eq)).toBe('5 == RT_DATA.MOP.AUTO')
  })

describe('tIA LAD readability', () => {
  it('widens columns to fit the longest label so text never overlaps wires', () => {
    const longName = 'DB310_OP030_User_Cmd.ByPass_Cylinder_Number_One'
    const projection = projectLadNetwork(network([
      part('access:long', 30, 'Access', { symbolPath: [longName] }),
      part('part:a', 21, 'Contact', { symbolPath: ['X'], pinNames: ['in', 'operand', 'out'] }),
      part('part:b', 22, 'Contact', { symbolPath: ['B'], pinNames: ['in', 'operand', 'out'] }),
      part('part:coil', 23, 'TrCoil', { symbolPath: ['D'], pinNames: ['in'] }),
    ], [
      { sourceKey: 'w:0', uId: 50, endpoints: [{ kind: 'IdentCon', uId: 30, name: '' }, { kind: 'NameCon', uId: 21, name: 'operand' }] },
      { sourceKey: 'w:1', uId: 51, endpoints: [{ kind: 'Powerrail', uId: null, name: '' }, { kind: 'NameCon', uId: 21, name: 'in' }] },
      { sourceKey: 'w:2', uId: 52, endpoints: [{ kind: 'NameCon', uId: 21, name: 'out' }, { kind: 'NameCon', uId: 23, name: 'in' }] },
      { sourceKey: 'w:3', uId: 53, endpoints: [{ kind: 'NameCon', uId: 22, name: 'out' }, { kind: 'NameCon', uId: 23, name: 'in' }] },
    ]), [])
    const a = projection.symbols.find(symbol => symbol.uId === 21)!
    expect(a.operands[0]?.label).toBe(longName)

    const layout = layoutLadNetwork(expandLadOrGates(projection))
    expect(layout.columnWidths[0]).toBeGreaterThanOrEqual(280)
    expect(layout.positions.get('part:coil')!.x).toBeGreaterThan(78 + 154)
    // 短标签列保持标准列距
    const compact = layoutLadNetwork(expandLadOrGates(projectLadNetwork(network([
      part('part:a', 21, 'Contact', { symbolPath: ['A'], pinNames: ['in', 'operand', 'out'] }),
      part('part:coil', 23, 'TrCoil', { symbolPath: ['D'], pinNames: ['in'] }),
    ]), [])))
    expect(compact.columnWidths[0]).toBe(154)
  })

  it('wraps long labels onto extra lines without ever truncating', () => {
    const lines = wrapLadLabelLines('OP030_CYL01_超长信号名称测试_一二三四五六七八九十_ABCDEFGHIJKLMNOPQRSTUVWXYZ_1234567890', 24)
    expect(lines.length).toBeGreaterThan(2)
    expect(lines.every(line => !line.endsWith('…'))).toBe(true)
    expect(lines.join('').replace(/ /g, '').length).toBeGreaterThanOrEqual(50)
  })

  it('builds the equivalent boolean expression for the network', () => {
    // 并联 = OR
    const parallel = network([
      part('part:a', 21, 'Contact', { symbolPath: ['A'], pinNames: ['in', 'operand', 'out'] }),
      part('part:b', 22, 'Contact', { symbolPath: ['B'], pinNames: ['in', 'operand', 'out'] }),
      part('part:or', 23, 'O', { pinNames: ['in1', 'in2', 'out'] }),
      part('part:coil', 24, 'TrCoil', { symbolPath: ['D'], pinNames: ['in'] }),
    ], [
      { sourceKey: 'e:1', uId: 81, endpoints: [{ kind: 'Powerrail', uId: null, name: '' }, { kind: 'NameCon', uId: 21, name: 'in' }] },
      { sourceKey: 'e:2', uId: 82, endpoints: [{ kind: 'Powerrail', uId: null, name: '' }, { kind: 'NameCon', uId: 22, name: 'in' }] },
      { sourceKey: 'e:3', uId: 83, endpoints: [{ kind: 'NameCon', uId: 21, name: 'out' }, { kind: 'NameCon', uId: 23, name: 'in1' }] },
      { sourceKey: 'e:4', uId: 84, endpoints: [{ kind: 'NameCon', uId: 22, name: 'out' }, { kind: 'NameCon', uId: 23, name: 'in2' }] },
      { sourceKey: 'e:5', uId: 85, endpoints: [{ kind: 'NameCon', uId: 23, name: 'out' }, { kind: 'NameCon', uId: 24, name: 'in' }] },
    ])
    expect(tiaLadNetworkExpression(parallel, [])).toBe('(A OR B)')

    // 串联 + 常闭 = AND + NOT
    const series = network([
      part('part:a', 21, 'Contact', { symbolPath: ['A'], pinNames: ['in', 'operand', 'out'] }),
      part('part:b', 22, 'Contact', { symbolPath: ['B'], negated: true, pinNames: ['in', 'operand', 'out'] }),
      part('part:coil', 23, 'TrCoil', { symbolPath: ['D'], pinNames: ['in'] }),
    ], [
      { sourceKey: 's:1', uId: 91, endpoints: [{ kind: 'Powerrail', uId: null, name: '' }, { kind: 'NameCon', uId: 21, name: 'in' }] },
      { sourceKey: 's:2', uId: 92, endpoints: [{ kind: 'NameCon', uId: 21, name: 'out' }, { kind: 'NameCon', uId: 22, name: 'in' }] },
      { sourceKey: 's:3', uId: 93, endpoints: [{ kind: 'NameCon', uId: 22, name: 'out' }, { kind: 'NameCon', uId: 23, name: 'in' }] },
    ])
    expect(tiaLadNetworkExpression(series, [])).toBe('(A AND NOT(B))')
  })
})
  it('places comparator pins on the rung axis like a contact', () => {
    const source = network([
      part('access:left', 31, 'Access', { symbolPath: ['RT_DATA', 'MOP', 'AUTO'] }),
      part('access:right', 32, 'Access', { constantValue: '5' }),
      part('part:contact', 21, 'Contact', { symbolPath: ['A'], pinNames: ['in', 'operand', 'out'] }),
      part('part:gt', 22, 'Gt', { pinNames: ['in1', 'in2', 'out'] }),
      part('part:coil', 23, 'TrCoil', { symbolPath: ['D'], pinNames: ['in'] }),
    ], [
      { sourceKey: 'g:0a', uId: 70, endpoints: [{ kind: 'IdentCon', uId: 31, name: '' }, { kind: 'NameCon', uId: 22, name: 'in1' }] },
      { sourceKey: 'g:0b', uId: 74, endpoints: [{ kind: 'IdentCon', uId: 32, name: '' }, { kind: 'NameCon', uId: 22, name: 'in2' }] },
      { sourceKey: 'g:1', uId: 71, endpoints: [{ kind: 'Powerrail', uId: null, name: '' }, { kind: 'NameCon', uId: 21, name: 'in' }] },
      { sourceKey: 'g:2', uId: 72, endpoints: [{ kind: 'NameCon', uId: 21, name: 'out' }, { kind: 'NameCon', uId: 22, name: 'in1' }] },
      { sourceKey: 'g:3', uId: 73, endpoints: [{ kind: 'NameCon', uId: 22, name: 'out' }, { kind: 'NameCon', uId: 23, name: 'in' }] },
    ])

    const expanded = expandLadOrGates(projectLadNetwork(source, []))
    const geometry = projectLadGeometry(expanded, layoutLadNetwork(expanded))

    // 比较器内联在梯级上：进出线都在 y=66 水平中轴，全程直线。
    // 表达式列按列宽自适应加宽到 183；折行预算另按窄邻列收紧（宽列宁可多折一行也不越界）
    expect(geometry.wires.map(wire => wire.points)).toEqual([
      '28,66 78,66',
      '190,66 232,66',
      '344,66 415,66',
      '415,66 653,66',
    ])
  })
})

describe('tIA LAD overlap regressions (2026-09 fix)', () => {
  it('grows row pitch to fit runtime judgment and value-status lines above wrapped labels', () => {
    // 旧缺陷：行距公式只数标签折行行数，监控态每个触点还会在标签上方多渲染
    // 判断行（>True/False）+ 值状态行，这 2 行未计入 → 顶部文字压进上一行元件。
    const longName = 'OP030_CYL01_FwdPos_Sensor_Long_Operand_Name'
    const source = network([
      part('access:long', 30, 'Access', { symbolPath: [longName] }),
      part('part:a', 21, 'Contact', { symbolPath: ['X'], pinNames: ['in', 'operand', 'out'] }),
      part('part:coil', 23, 'TrCoil', { symbolPath: ['D'], pinNames: ['in'] }),
    ], [
      { sourceKey: 'w:0', uId: 50, endpoints: [{ kind: 'IdentCon', uId: 30, name: '' }, { kind: 'NameCon', uId: 21, name: 'operand' }] },
      { sourceKey: 'w:1', uId: 51, endpoints: [{ kind: 'Powerrail', uId: null, name: '' }, { kind: 'NameCon', uId: 21, name: 'in' }] },
      { sourceKey: 'w:2', uId: 52, endpoints: [{ kind: 'NameCon', uId: 21, name: 'out' }, { kind: 'NameCon', uId: 23, name: 'in' }] },
    ])

    const offline = layoutLadNetwork(expandLadOrGates(projectLadNetwork(source, [])))
    const online = layoutLadNetwork(expandLadOrGates(projectLadNetwork(source, [
      runtime('part:a', 'True', 'Good'),
      runtime('access:long', 'True', 'Good'),
    ])))

    // 折行预算按窄邻列收紧后：离线 4 行 / 在线 6 行（+判断行+值状态行）；
    // 首行顶边随向上堆叠高度下移（64→88），行距 96 不变 → 高差 24
    expect(online.height - offline.height).toBe(24)
  })

  it('spaces multi-pin box operands on distinct Ys instead of clamping them onto one line', () => {
    // 旧缺陷：operandY = min(pinIndex,3)*14+12 把第 5 根及以后的引脚标签全部钳到 y=54，
    // 操作数文字完全重叠；盒高也不随引脚数增长。
    const source = network([
      part('part:a', 21, 'Contact', { symbolPath: ['A'], pinNames: ['in', 'operand', 'out'] }),
      part('part:box', 22, 'Add', { pinNames: ['in1', 'in2', 'in3', 'in4', 'in5', 'out'] }),
      part('access:o5', 32, 'Access', { symbolPath: ['Fifth_Operand_Of_The_Box'] }),
    ], [
      { sourceKey: 'w:0', uId: 61, endpoints: [{ kind: 'Powerrail', uId: null, name: '' }, { kind: 'NameCon', uId: 21, name: 'in' }] },
      { sourceKey: 'w:1', uId: 62, endpoints: [{ kind: 'NameCon', uId: 21, name: 'out' }, { kind: 'NameCon', uId: 22, name: 'in5' }] },
      { sourceKey: 'w:2', uId: 63, endpoints: [{ kind: 'IdentCon', uId: 32, name: '' }, { kind: 'NameCon', uId: 22, name: 'in5' }] },
    ])

    const expanded = expandLadOrGates(projectLadNetwork(source, []))
    const layout = layoutLadNetwork(expanded)
    const box = expanded.symbols.find(symbol => symbol.partName === 'Add')!
    const boxPosition = layout.positions.get('part:box')!

    // 6 引脚盒：in5 在第 5 档（y=68），盒高必须包住最后一根引脚（out 在 y=94 → ≥108）
    expect(tiaLadSymbolHeight(box)).toBeGreaterThanOrEqual(96)
    const geometry = projectLadGeometry(expanded, layout)
    expect(geometry.wires.some(wire => wire.points.endsWith(`${boxPosition.x},${boxPosition.y + 68}`))).toBe(true)
  })

  it('caps the wrap budget of a wide column by its narrow neighbour so the first gap keeps clear', () => {
    // 2026-09 二次修复：宽列只按本列宽折行时，长折行文字会向左探过列界、
    // 压到左侧窄列（用户实测：第一个串行触点与右侧元件间隔过小、文字重合）。
    // 修复后宽列的每行字符预算 = min(本列宽, 相邻列宽) - 44px 净空。
    const projection = projectLadNetwork(network([
      part('access:short', 30, 'Access', { symbolPath: ['SQ91'] }),
      part('access:long', 31, 'Access', { symbolPath: ['DB310_OP030_User_Cmd.ByPass_Cylinder_Number_One'] }),
      part('part:first', 21, 'Contact', { pinNames: ['in', 'operand', 'out'] }),
      part('part:second', 22, 'Contact', { pinNames: ['in', 'operand', 'out'] }),
      part('part:coil', 23, 'TrCoil', { pinNames: ['in'] }),
    ], [
      { sourceKey: 'n:0', uId: 60, endpoints: [{ kind: 'IdentCon', uId: 30, name: '' }, { kind: 'NameCon', uId: 21, name: 'operand' }] },
      { sourceKey: 'n:1', uId: 61, endpoints: [{ kind: 'IdentCon', uId: 31, name: '' }, { kind: 'NameCon', uId: 22, name: 'operand' }] },
      { sourceKey: 'n:2', uId: 62, endpoints: [{ kind: 'Powerrail', uId: null, name: '' }, { kind: 'NameCon', uId: 21, name: 'in' }] },
      { sourceKey: 'n:3', uId: 63, endpoints: [{ kind: 'NameCon', uId: 21, name: 'out' }, { kind: 'NameCon', uId: 22, name: 'in' }] },
      { sourceKey: 'n:4', uId: 64, endpoints: [{ kind: 'NameCon', uId: 22, name: 'out' }, { kind: 'NameCon', uId: 23, name: 'in' }] },
    ]), [])

    const layout = layoutLadNetwork(expandLadOrGates(projection))
    // 第二列因长标签加宽到上限 280，第一列保持标准 154 —— 但第二列的折行预算被窄邻列钳制
    expect(layout.columnWidths[1]).toBe(280)
    expect(layout.columnWidths[0]).toBe(154)
    expect(layout.columnMaxChars[1]).toBeLessThanOrEqual(Math.floor((154 - 44) / 6.9))
    // 折行后的文字宽度不超过 窄列宽-净空 → 两列文字之间至少保有 ~44px 净空
    expect(layout.columnMaxChars[1] * 6.9).toBeLessThanOrEqual(154 - 40)
  })

  it('sizes row pitch by the next row’s stacked text so lower-branch labels never touch the branch above', () => {
    // 2026-09 二次修复：旧行距公式用"本行行数"近似"下一行行数"，上行矮、下行文字多时
    // 短缺 6~54px → 下支路的多行标签顶进上支路元件。修复后行距 = 上行盒底 + 12 净空 + 下行向上堆叠高度。
    const longName = 'OP030_CYL01_FwdPos_Sensor_Long_Operand_Name'
    const source = network([
      part('access:long', 30, 'Access', { symbolPath: [longName] }),
      part('part:top', 21, 'Contact', { symbolPath: ['Top_Short'], pinNames: ['in', 'out'] }),
      part('part:bottom', 22, 'Contact', { pinNames: ['in', 'out'] }),
      part('part:coil', 23, 'TrCoil', { pinNames: ['in'] }),
    ], [
      { sourceKey: 'p:0', uId: 70, endpoints: [{ kind: 'IdentCon', uId: 30, name: '' }, { kind: 'NameCon', uId: 22, name: 'operand' }] },
      { sourceKey: 'p:1', uId: 71, endpoints: [{ kind: 'Powerrail', uId: null, name: '' }, { kind: 'NameCon', uId: 21, name: 'in' }] },
      { sourceKey: 'p:2', uId: 72, endpoints: [{ kind: 'NameCon', uId: 21, name: 'out' }, { kind: 'NameCon', uId: 23, name: 'in' }] },
      { sourceKey: 'p:3', uId: 73, endpoints: [{ kind: 'NameCon', uId: 22, name: 'out' }, { kind: 'NameCon', uId: 23, name: 'in' }] },
    ])

    const layout = layoutLadNetwork(expandLadOrGates(projectLadNetwork(source, [])))
    const top = layout.positions.get('part:top')!
    const bottom = layout.positions.get('part:bottom')!
    // 下支路 44 字符标签在 15 字符预算下折 4 行（向上 56px）：
    // 行距 = 上行盒底 54 + 净空 12 + 56 = 122（旧公式只给 96 → 顶进上一行）
    expect(bottom.y - top.y).toBe(122)
    // 下支路最上一行文字（bottom.y - 56）不再顶到上支路盒底（top.y + 54）
    expect(bottom.y - 56).toBeGreaterThanOrEqual(top.y + 54)
  })
})

describe('localSymbolMap：FB 局部变量按全局符号路径附加显示（2026-10-07）', () => {
  const localAccess = part('access:auto', 21, 'Access', {
    scope: 'LocalVariable',
    symbolPath: ['S_AutoRunMode'],
  })
  // 全局变量：即使 map 里恰好有同名 key（scope 守卫），也必须按原样显示
  const globalAccess = part('access:global', 22, 'Access', {
    scope: 'GlobalVariable',
    symbolPath: ['M010_2_Action', 'Sensor_On[37]'],
  })
  const contact = part('part:contact', 29, 'Contact', { pinNames: ['operand', 'out'] })
  const coil = part('part:coil', 23, 'TrCoil', { pinNames: ['in'] })
  const source = network([localAccess, globalAccess, contact, coil], [
    { sourceKey: 'w:1', uId: 40, endpoints: [{ kind: 'IdentCon', uId: 21, name: '' }, { kind: 'NameCon', uId: 29, name: 'operand' }] },
    { sourceKey: 'w:2', uId: 41, endpoints: [{ kind: 'Powerrail', uId: null, name: '' }, { kind: 'NameCon', uId: 29, name: 'in' }] },
    { sourceKey: 'w:3', uId: 42, endpoints: [{ kind: 'NameCon', uId: 29, name: 'out' }, { kind: 'NameCon', uId: 23, name: 'in' }] },
  ])
  const symbolMap = {
    'S_AutoRunMode': 'IDB150_OP1030_电芯上线扫码枪自动流程.S_AutoRunMode',
    'M010_2_Action.Sensor_On[37]': 'SHOULD_NOT_APPEAR',
  }

  it('局部变量命中映射：原局部名保留，全局路径放括号内', () => {
    const projection = projectLadNetwork(source, [], symbolMap)
    const contactSymbol = projection.symbols.find(symbol => symbol.uId === 29)!
    expect(contactSymbol.label).toBe('S_AutoRunMode（IDB150_OP1030_电芯上线扫码枪自动流程.S_AutoRunMode）')
    expect(contactSymbol.operands[0].label).toBe('S_AutoRunMode（IDB150_OP1030_电芯上线扫码枪自动流程.S_AutoRunMode）')
  })

  it('全局变量不受映射影响（scope 守卫）', () => {
    const projection = projectLadNetwork(source, [], symbolMap)
    // globalAccess 未附着到任何功能部件 → 落 accessFallbacks，按原样显示
    const fallback = projection.accessFallbacks.find(item => item.uId === 22)!
    expect(fallback.label).toBe('M010_2_Action.Sensor_On[37]')
  })

  it('未传映射时行为与原来完全一致', () => {
    const projection = projectLadNetwork(source, [])
    const contactSymbol = projection.symbols.find(symbol => symbol.uId === 29)!
    expect(contactSymbol.label).toBe('S_AutoRunMode')
  })

  it('表达式文本同样携带括号内的全局路径', () => {
    const expression = tiaLadNetworkExpression(source, [], symbolMap)
    expect(expression).toContain('S_AutoRunMode（IDB150_OP1030_电芯上线扫码枪自动流程.S_AutoRunMode）')
  })
})
