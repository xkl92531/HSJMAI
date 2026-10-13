using System.Threading;
using HSJM.AI.AppLayer.LineUp.Models;
using HSJM.AI.AppLayer.LineUp.Parsing;
using Xunit;

namespace HSJM.AI.LineUp.Tests;

/// <summary>
/// LineUpGraphXmlParser 符号路径渲染回归测试。
/// 2026-09-28 FB350_*AutoGraph（批次 4625039937220251648）：
/// 数组下标本身又是数组元素 StackingData[DB57_UserDefine.Int[12]]，
/// 下标 Symbol 内嵌第二个 Access，曾因 allAccesses.Count != 1 整批 InvalidGraphStructure。
/// </summary>
public sealed class LineUpGraphXmlParserSymbolTests
{
    private static LineUpGraphDocument Parse(string content) =>
        new LineUpGraphXmlParser().ParseAsync(
            new LineUpGraphSourceFile
            {
                FileName = "FB350_TestAutoGraph.xml",
                Content = content,
            },
            CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void ParseNestedArrayIndexSymbol_RendersIndexExpression()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <SW.Blocks.FB>
    <AttributeList>
      <Name>FB350_TestAutoGraph</Name>
      <Number>350</Number>
      <ProgrammingLanguage>GRAPH</ProgrammingLanguage>
    </AttributeList>
    <ObjectList>
      <SW.Blocks.CompileUnit>
        <AttributeList>
          <NetworkSource>
            <Graph>
              <Sequence Name=""S1"">
                <Title>Auto</Title>
                <Steps>
                  <Step Number=""1"" Init=""true"" />
                </Steps>
                <Transitions>
                  <Transition Number=""50"" ProgrammingLanguage=""LAD"" IsMissing=""false"">
                    <FlgNet>
                      <Parts>
                        <Part Name=""Access"" UId=""21"">
                          <Symbol>
                            <Component Name=""StackingData"" AccessModifier=""Array"">
                              <Access Scope=""GlobalVariable"">
                                <Symbol>
                                  <Component Name=""DB57_UserDefine"" />
                                  <Component Name=""Int"" AccessModifier=""Array"">
                                    <Access Scope=""LiteralConstant"">
                                      <Constant>
                                        <ConstantType>DInt</ConstantType>
                                        <ConstantValue>12</ConstantValue>
                                      </Constant>
                                    </Access>
                                  </Component>
                                </Symbol>
                              </Access>
                            </Component>
                          </Symbol>
                        </Part>
                        <Part Name=""Eq"" UId=""35"" />
                      </Parts>
                    </FlgNet>
                  </Transition>
                </Transitions>
              </Sequence>
            </Graph>
          </NetworkSource>
        </AttributeList>
      </SW.Blocks.CompileUnit>
    </ObjectList>
  </SW.Blocks.FB>
</Document>";

        var document = Parse(xml);

        Assert.Equal("FB350_TestAutoGraph", document.BlockName);
        var transition = Assert.Single(document.Sequences[0].Transitions);
        var network = Assert.Single(transition.ConditionNetworks);
        var accessPart = Assert.Single(network.Parts, part => part.SymbolPath.Count > 0);
        Assert.Equal("StackingData[DB57_UserDefine.Int[12]]", string.Join(".", accessPart.SymbolPath));
    }

    [Fact]
    public void ParseLiteralArrayIndexSymbol_StillRendersLiteralIndex()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <SW.Blocks.FB>
    <AttributeList>
      <Name>FB350_TestAutoGraph</Name>
      <Number>350</Number>
      <ProgrammingLanguage>GRAPH</ProgrammingLanguage>
    </AttributeList>
    <ObjectList>
      <SW.Blocks.CompileUnit>
        <AttributeList>
          <NetworkSource>
            <Graph>
              <Sequence Name=""S1"">
                <Title>Auto</Title>
                <Steps>
                  <Step Number=""1"" Init=""true"" />
                </Steps>
                <Transitions>
                  <Transition Number=""1"" ProgrammingLanguage=""LAD"" IsMissing=""false"">
                    <FlgNet>
                      <Parts>
                        <Part Name=""Access"" UId=""21"">
                          <Symbol>
                            <Component Name=""OKtableData"" AccessModifier=""Array"">
                              <Access Scope=""LiteralConstant"">
                                <Constant>
                                  <ConstantType>Int</ConstantType>
                                  <ConstantValue>3</ConstantValue>
                                </Constant>
                              </Access>
                            </Component>
                          </Symbol>
                        </Part>
                      </Parts>
                    </FlgNet>
                  </Transition>
                </Transitions>
              </Sequence>
            </Graph>
          </NetworkSource>
        </AttributeList>
      </SW.Blocks.CompileUnit>
    </ObjectList>
  </SW.Blocks.FB>
</Document>";

        var document = Parse(xml);

        var transition = Assert.Single(document.Sequences[0].Transitions);
        var network = Assert.Single(transition.ConditionNetworks);
        var accessPart = Assert.Single(network.Parts, part => part.SymbolPath.Count > 0);
        Assert.Equal("OKtableData[3]", string.Join(".", accessPart.SymbolPath));
    }

    [Fact]
    public void ParseSequenceEndConnection_SkipsTerminatorEdgeAndExemptsTopology()
    {
        // FB151（PLC2，批次 4625046158325190656/4625048850988666880）：最终转换直连
        // <NodeTo><EndConnection/></NodeTo>。终止边应跳过且记入 EndAnchors；
        // 归一化后该转换不得被 TransitionWithoutOutgoingEdge/GraphTopologyBlocking 阻断。
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <SW.Blocks.FB>
    <AttributeList>
      <Name>FB151_TestGraph</Name>
      <Number>151</Number>
      <ProgrammingLanguage>GRAPH</ProgrammingLanguage>
    </AttributeList>
    <ObjectList>
      <SW.Blocks.CompileUnit>
        <AttributeList>
          <NetworkSource>
            <Graph>
              <Sequence Name=""S1"">
                <Title>Sequence 1</Title>
                <Steps>
                  <Step Number=""1"" Init=""true"" />
                  <Step Number=""2"" />
                </Steps>
                <Transitions>
                  <Transition Number=""1"" ProgrammingLanguage=""LAD"" IsMissing=""false"" />
                  <Transition Number=""60"" ProgrammingLanguage=""LAD"" IsMissing=""false"" />
                </Transitions>
                <Connections>
                  <Connection>
                    <NodeFrom>
                      <StepRef Number=""1"" />
                    </NodeFrom>
                    <NodeTo>
                      <TransitionRef Number=""1"" />
                    </NodeTo>
                    <LinkType>Direct</LinkType>
                  </Connection>
                  <Connection>
                    <NodeFrom>
                      <TransitionRef Number=""1"" />
                    </NodeFrom>
                    <NodeTo>
                      <StepRef Number=""2"" />
                    </NodeTo>
                    <LinkType>Direct</LinkType>
                  </Connection>
                  <Connection>
                    <NodeFrom>
                      <StepRef Number=""2"" />
                    </NodeFrom>
                    <NodeTo>
                      <TransitionRef Number=""60"" />
                    </NodeTo>
                    <LinkType>Direct</LinkType>
                  </Connection>
                  <Connection>
                    <NodeFrom>
                      <TransitionRef Number=""60"" />
                    </NodeFrom>
                    <NodeTo>
                      <EndConnection />
                    </NodeTo>
                    <LinkType>Direct</LinkType>
                  </Connection>
                </Connections>
              </Sequence>
            </Graph>
          </NetworkSource>
        </AttributeList>
      </SW.Blocks.CompileUnit>
    </ObjectList>
  </SW.Blocks.FB>
</Document>";

        var document = Parse(xml);

        var sequence = Assert.Single(document.Sequences);
        Assert.Equal(3, sequence.RawConnections.Count);
        var anchor = Assert.Single(sequence.EndAnchors);
        Assert.Equal(HSJM.AI.AppLayer.LineUp.Models.LineUpGraphEndpointKind.Transition, anchor.Kind);
        Assert.Equal(60, anchor.Number);

        var preview = new LineUpGraphNormalizer().Normalize(document);
        Assert.DoesNotContain(preview.Issues, issue =>
            issue.IssueType is "TransitionWithoutOutgoingEdge" or "GraphTopologyBlocking");
        var blocking = preview.Issues.Where(issue => issue.Severity == "Blocking").ToList();
        Assert.Empty(blocking);
    }

    [Fact]
    public void NormalizeCrossSequenceSubroutine_DoesNotBlockTopology()
    {
        // FB250（PLC2，批次 4625052655314669568）：主序列 T184 --Jump--> 子序列 Step204（调用）、
        // 子序列 T222 --Jump--> 主序列 Step2（返回）；子程序无 Init 步、且存在无入边前置步
        // （200→T200→204）。曾报 DanglingEdge×2 + TransitionWithoutOutgoingEdge×2 +
        // MissingInitialStep 三连 Blocking。跨序列接线分流 + 入口/孤儿步播种后应零阻断。
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <SW.Blocks.FB>
    <AttributeList>
      <Name>FB250_TestGraph</Name>
      <Number>250</Number>
      <ProgrammingLanguage>GRAPH</ProgrammingLanguage>
    </AttributeList>
    <ObjectList>
      <SW.Blocks.CompileUnit>
        <AttributeList>
          <NetworkSource>
            <Graph>
              <Sequence Name=""Main"">
                <Title>Main</Title>
                <Steps>
                  <Step Number=""1"" Init=""true"" />
                  <Step Number=""2"" />
                </Steps>
                <Transitions>
                  <Transition Number=""1"" ProgrammingLanguage=""LAD"" IsMissing=""false"" />
                  <Transition Number=""184"" ProgrammingLanguage=""LAD"" IsMissing=""false"" />
                </Transitions>
                <Connections>
                  <Connection>
                    <NodeFrom><StepRef Number=""1"" /></NodeFrom>
                    <NodeTo><TransitionRef Number=""1"" /></NodeTo>
                    <LinkType>Direct</LinkType>
                  </Connection>
                  <Connection>
                    <NodeFrom><TransitionRef Number=""1"" /></NodeFrom>
                    <NodeTo><StepRef Number=""2"" /></NodeTo>
                    <LinkType>Direct</LinkType>
                  </Connection>
                  <Connection>
                    <NodeFrom><TransitionRef Number=""184"" /></NodeFrom>
                    <NodeTo><StepRef Number=""204"" /></NodeTo>
                    <LinkType>Jump</LinkType>
                  </Connection>
                  <Connection>
                    <NodeFrom><StepRef Number=""2"" /></NodeFrom>
                    <NodeTo><TransitionRef Number=""184"" /></NodeTo>
                    <LinkType>Direct</LinkType>
                  </Connection>
                </Connections>
              </Sequence>
              <Sequence Name=""Sub"">
                <Title>Sub</Title>
                <Steps>
                  <Step Number=""200"" />
                  <Step Number=""204"" />
                </Steps>
                <Transitions>
                  <Transition Number=""200"" ProgrammingLanguage=""LAD"" IsMissing=""false"" />
                  <Transition Number=""222"" ProgrammingLanguage=""LAD"" IsMissing=""false"" />
                </Transitions>
                <Connections>
                  <Connection>
                    <NodeFrom><StepRef Number=""200"" /></NodeFrom>
                    <NodeTo><TransitionRef Number=""200"" /></NodeTo>
                    <LinkType>Direct</LinkType>
                  </Connection>
                  <Connection>
                    <NodeFrom><TransitionRef Number=""200"" /></NodeFrom>
                    <NodeTo><StepRef Number=""204"" /></NodeTo>
                    <LinkType>Direct</LinkType>
                  </Connection>
                  <Connection>
                    <NodeFrom><StepRef Number=""204"" /></NodeFrom>
                    <NodeTo><TransitionRef Number=""222"" /></NodeTo>
                    <LinkType>Direct</LinkType>
                  </Connection>
                  <Connection>
                    <NodeFrom><TransitionRef Number=""222"" /></NodeFrom>
                    <NodeTo><StepRef Number=""2"" /></NodeTo>
                    <LinkType>Jump</LinkType>
                  </Connection>
                </Connections>
              </Sequence>
            </Graph>
          </NetworkSource>
        </AttributeList>
      </SW.Blocks.CompileUnit>
    </ObjectList>
  </SW.Blocks.FB>
</Document>";

        var document = Parse(xml);
        var preview = new LineUpGraphNormalizer().Normalize(document);

        Assert.Equal(2, preview.Sequences.Count);
        var blocking = preview.Issues.Where(issue => issue.Severity == "Blocking").ToList();
        Assert.Empty(blocking);
        Assert.Contains(preview.Issues, issue => issue.IssueType == "CrossSequenceEdge");
    }
}
