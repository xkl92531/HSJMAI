#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.AppLayer.Agent.Services;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

public sealed class BusinessTemplateInference(PipelineModelCatalog catalog,IAiModelPurposeResolver purposeResolver):ISelfScopedAutoInject
{
    LLM_ModelEntity? model;string hash="uninitialized";
    long id;
    long revision;
    static readonly JsonSerializerOptions Json=new(){PropertyNameCaseInsensitive=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
    public string Version=>$"business-block-contract-18:stable-slices3:monitor-ir6:free-json-repair:temperature0:no-thinking:tokens2000:{BusinessModelProjection.Version}:{BusinessNumericEvidence.Version}:{id}:{revision}:{hash}";
    public async Task InitializeAsync(CancellationToken token) {
        var selection=await purposeResolver.ResolveAsync(AiModelPurposes.IndependentPlc,token);
        var next=selection.Model;var nextHash=PipelineModelInference.ModelFingerprint(next);
        if(model!=null&&(hash!=nextHash||selection.Revision!=revision))throw new PipelineException("ModelChangedDuringRun");
        id=next.Id;revision=selection.Revision;
        // Request-local determinism only: never update the shared model configuration in the database.
        var parameters=string.IsNullOrWhiteSpace(next.CustomRequestParams)?new JsonObject():JsonNode.Parse(next.CustomRequestParams)!.AsObject();
        parameters["temperature"]=0;
        var chat=parameters["chat_template_kwargs"] as JsonObject??new JsonObject();chat["enable_thinking"]=false;parameters["chat_template_kwargs"]=chat;
        next.CustomRequestParams=parameters.ToJsonString();next.MaxTokens=12000;model=next;hash=nextHash;
    }
    sealed record Proposal(string Operator,string? Source,IReadOnlyList<string> Members,string Explanation,IReadOnlyList<string> EvidenceIds,
        IReadOnlyList<BusinessCondition> Conditions,string Applicability,long? SettlingTimeMs=null,string? SettlingTimeSignal=null,
        double SettlingTimeScale=1,LogicTerm? ApplicabilityCondition=null);
    sealed record Analysis(string Summary,IReadOnlyList<Proposal> Candidates);
    sealed record ReviewVote(string Reason,int Index,bool Accept,IReadOnlyList<Proposal> Replacements);
    sealed record ReviewResponse(IReadOnlyList<ReviewVote> Reviews);
    sealed record MonitorJudgement(bool Supported,string Reason,IReadOnlyList<string>? Limitations=null,string? FailureCode=null);
    public async Task<BusinessTemplateAnalysis> AnalyzeAsync(BusinessDescription template,CancellationToken token) {
        if(model==null)await InitializeAsync(token);
        var formals=(template.Symbols??[]).Where(s=>s.BlockId==template.BlockId&&s.Section is "Input" or "Output" or "InOut")
            .Where(s=>!s.Path.Contains('.')&&!s.Path.Contains('[')).ToArray();
        var allowed=template.SignalAliases.Concat(formals.Select(s=>new KeyValuePair<string,string>(s.Alias,"formal:"+s.Alias)))
            .GroupBy(p=>p.Key,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.First().Value,StringComparer.Ordinal);
        var view=template with{SignalAliases=allowed};
        var decisions=StableSliceCandidates(template).Select(c=>ValidateTemplateCandidate(view,c))
            .Select(d=>d.Status=="Validated"?d with{Code="TemplateCodeDerivedStableSlice"}:d).ToList();
        var summaries=new List<string>{"单写普通线圈的公开关系经布尔传播固定为StableDiagnosticSnapshot，不作为瞬时程序证明。"};
        var accepted=new List<MonitorChain>();
        var chains=BuildMonitorChains(template);
        var partial=chains.Count>32;
        if(partial)summaries.Add("TemplateMonitorBudgetExceeded: 此次仅处理前32条监视链，其它关系未声称完成。");
        foreach(var chain in chains.Take(32)) {
            token.ThrowIfCancellationRequested();
            var ir=new {
                activityOutput=chain.ActivityOutput,completionOutput=chain.CompletionOutput,
                exactGate=new{gate=chain.PendingGate,expression=$"AND({chain.ActivityState}, NOT({chain.CompletionState}))"},
                publicMapping=new{activityState=chain.ActivityState,completionState=chain.CompletionState,readGates=chain.OutputReadGates},
                chain.Counter,extraConditions=chain.Conditions,completionRequiresTrueInputs=chain.CompletionTrueInputs,completionRequiresFalseInputs=chain.CompletionFalseInputs,
                instructionSemanticsProvedByCode=new{chain.EdgeMemoryWriters,chain.ResetBehaviors},
                instructions=chain.CounterFacts.Select(f=>new{f.Instruction,f.NetworkOrder,f.DataType,f.Inputs,f.Enable,f.Precondition,f.Outputs,f.Limitations}),
                scope="StableDiagnosticSnapshot requires an upstream-confirmed stable diagnostic interval, caller-validated normal numeric configuration and normally running monitoring logic. Numeric reachability for arbitrary dynamic thresholds/initial values is NOT proved; never infer milliseconds or clock frequency. All counter writes including reset are retained."
            };
            var proposal=await SafeJudgeMonitorAsync(ir,null,token);
            var review=proposal.Supported?await SafeJudgeMonitorAsync(ir,proposal.Reason,token):proposal;
            var failure=proposal.FailureCode??review.FailureCode;partial|=failure is not null;
            var supported=failure is null&&proposal.Supported&&review.Supported;
            var reason=review.Reason+" 边界："+string.Join("；",review.Limitations??[]);
            summaries.Add($"{chain.ActivityOutput} → {chain.CompletionOutput}: {reason}");
            if(supported)accepted.Add(chain);
            var explanation="由真实增量/阈值/复位监视链形成正常稳定期望；方向及公开绑定由代码固定。"+reason;
            var candidates=new List<BusinessCandidate>{new("IMPLIES",[new(chain.ActivityOutput,"Source"),new(chain.CompletionOutput,"Target")],explanation,chain.EvidenceIds,chain.Conditions,"StableDiagnosticSnapshot")};
            if(chain.CompletionTrueInputs.Count>0)candidates.Add(new("IMPLIES",new[]{new PipelineSignal(chain.ActivityOutput,"Source")}.Concat(chain.CompletionTrueInputs.Select(p=>new PipelineSignal(p,"Target"))).ToArray(),explanation,chain.EvidenceIds,chain.Conditions,"StableDiagnosticSnapshot"));
            if(chain.CompletionFalseInputs.Count>0)candidates.Add(new("REQUIRES_0",new[]{new PipelineSignal(chain.ActivityOutput,"Source")}.Concat(chain.CompletionFalseInputs.Select(p=>new PipelineSignal(p,"Target"))).ToArray(),explanation,chain.EvidenceIds,chain.Conditions,"StableDiagnosticSnapshot"));
            foreach(var candidate in candidates) {
                var validation=ValidateTemplateCandidate(view,candidate);
                decisions.Add(!supported?new(candidate,"Rejected",failure is null?"TemplateMonitorNotSupported:"+reason:"TemplateMonitorModelUnavailable:"+failure):validation.Status=="Validated"?validation with{Code="TemplateModelCodeReviewAccepted:"+reason}:validation);
            }
        }
        var reciprocal=accepted.Where(c=>c.CompletionTrueInputs.Count==1&&c.CompletionFalseInputs.Count==1).ToArray();
        if(reciprocal.Length>0) {
            var judgement=await SafeJudgeMonitorAsync(new{businessQuestion="每条正常稳定监视期望已独立复核：该活动及其允许条件成立，要求一个反馈Input真且另一个反馈Input假。是否支持仅在该条活动及其条件成立时，两反馈互斥的诊断表达？各链分别成立，不能要求另一方向活动有可读输出，不能推广到无活动时。",
                reviewedExpectations=reciprocal.Select(c=>new{c.ActivityOutput,trueInput=c.CompletionTrueInputs[0],falseInput=c.CompletionFalseInputs[0],c.Conditions})},null,token,feedbackComposition:true);
            partial|=judgement.FailureCode is not null;
            foreach(var chain in reciprocal) {
                var candidate=new BusinessCandidate("AT_MOST_ONE",new[]{chain.CompletionTrueInputs[0],chain.CompletionFalseInputs[0]}.Order(StringComparer.Ordinal).Select(a=>new PipelineSignal(a,"Member")).ToArray(),
                    "该方向活动及允许条件成立时的正常稳定反馈互斥；仅为已复核命令到反馈期望的等价诊断表达，不推广为无条件物理互斥。"+judgement.Reason,
                    chain.EvidenceIds,chain.Conditions,"StableDiagnosticSnapshot",ApplicabilityCondition:LogicTerm.Signal(chain.ActivityOutput));
                var validation=ValidateTemplateCandidate(view,candidate);
                decisions.Add(!judgement.Supported?new(candidate,"Rejected",judgement.FailureCode is null?"TemplateMonitorNotSupported:"+judgement.Reason:"TemplateMonitorModelUnavailable:"+judgement.FailureCode):validation.Status=="Validated"?validation with{Code="TemplateModelCodeReviewAccepted:GuardedBusinessComposition:"+judgement.Reason}:validation);
            }
        }
        return new(template,Version,string.Join("\n",summaries),decisions,partial?"PartiallyAnalyzedNotInstantiated":"AnalyzedNotInstantiated");
    }
    async Task<MonitorJudgement> SafeJudgeMonitorAsync(object ir,string? previous,CancellationToken token,bool feedbackComposition=false) {
        try {return await JudgeMonitorAsync(ir,previous,token,feedbackComposition);}
        catch(Exception ex)when(!token.IsCancellationRequested&&(ex is PipelineException or JsonException or System.Net.Http.HttpRequestException or OperationCanceledException)) {
            var code=ex is PipelineException p?p.Code:ex is JsonException?"InvalidTemplateMonitorJson":ex is OperationCanceledException?"TemplateMonitorTimeout":"ModelTransportFailure";
            return new(false,"监视链模型评估未完成，不能据此判定关系不成立。",[],code);
        }
    }
    async Task<MonitorJudgement> JudgeMonitorAsync(object ir,string? previous,CancellationToken token,bool feedbackComposition=false) {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var requestModel=RequestModel(0);requestModel.MaxTokens=2000;
        var instructions=feedbackComposition?"""
            评价已审查监视期望的一种业务诊断表达。输入每条活动→完成/反馈期望已通过计数监视链及独立复核。
            本次不重新分析计数器，只判断：在正常且充分稳定的诊断状态，该条活动及其允许条件成立要求X且非Y，是否可表达为该活动门控下X/Y互斥？
            每条链单独成立，不需要另一方向也有可读输出。必须保留该活动及其允许条件；不得变成无活动时的输入互斥，不声称所有故障输入下程序恒真。
            只返回短JSON：{"supported":true,"reason":"具体业务判断","limitations":["边界"]}。不输出Markdown。
            """:"""
            只判断一条已由代码锁定接线与公开绑定的PLC监视事实。变量别名没有物理含义，不猜命名。
            当“活动 AND NOT完成”使数值递增，计数达到阈值产生异常，且复位/允许条件与此监视一致时，可支持正常充分稳定后的“活动应完成”诊断期望。
            阈值比较的异常是违反期望的结果，不能当希望达到的反馈。此期望不是所有故障输入下的程序恒真。
            必须考虑所有计数写入、复位、EN/pre和边沿；若计数可在pending期间无条件反复清零或另有无法表达的禁用条件，应不支持并明确缺口。
            UnknownWriteOrder只说明单次扫描终值未证明；不能仅因此否定已知边沿复位下跨扫描的正常监视意图。也不能忽略真实无条件覆盖。
            增量与边沿复位可能在首次pending的同一扫描发生，二者互斥未经证明，禁止声称它们互斥；只可区分边沿复位与每扫描无条件覆盖。
            instructionSemanticsProvedByCode是依据真实写库存和布尔代入锁定的指令事实：记忆位唯一writer且pending/non-error边沿输入为常量时，正常求值一次后不会因稳定高电平持续产生上升沿。不得重新把它猜成每扫描清零。
            时钟频率未知时只保留StableDiagnosticSnapshot，不猜毫秒；评估前由上游确认充分稳定且监视逻辑正常运行。
            若收到previous，这是待批判的模型意见，不是事实；重新逐项核对IR再决定。
            只返回一个短JSON对象，不输出候选或改信号：{"supported":true,"reason":"不超过160字的具体判断依据","limitations":["简短边界"]}。不输出Markdown。
            """;
        for(var attempt=0;;attempt++) {
            try {
                var response=await catalog.CompleteAsync(requestModel,instructions,JsonSerializer.Serialize(new{ir,previous},Json),timeout.Token,
                    attempt==0?null:BusinessOutputSchema.MonitorJudgement());
                using var parsed=JsonDocument.Parse(response);
                if(parsed.RootElement.ValueKind!=JsonValueKind.Object)throw new JsonException("Expected judgement object");
                var supportedFields=parsed.RootElement.EnumerateObject().Where(p=>p.Name.Equals("supported",StringComparison.OrdinalIgnoreCase)).ToArray();
                if(supportedFields.Length!=1||supportedFields[0].Value.ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw new JsonException("Explicit boolean judgement required");
                var judgement=JsonSerializer.Deserialize<MonitorJudgement>(response,Json)??throw new JsonException("Missing judgement");
                if(string.IsNullOrWhiteSpace(judgement.Reason))throw new JsonException("Missing reason");return judgement with{FailureCode=null};
            }catch(Exception ex)when(attempt==0&&(ex is JsonException||ex is PipelineException p&&p.Code=="IncompleteModelResponse")) { }
        }
    }
    LLM_ModelEntity RequestModel(double temperature) {
        var requestModel=JsonSerializer.Deserialize<LLM_ModelEntity>(JsonSerializer.Serialize(model,Json),Json)!;
        var parameters=JsonNode.Parse(requestModel.CustomRequestParams)!.AsObject();parameters["temperature"]=temperature;
        requestModel.CustomRequestParams=parameters.ToJsonString();return requestModel;
    }
    static BusinessCandidate ToCandidate(Proposal p) {
        var role=p.Operator is "IMPLIES" or "REQUIRES_0" or "MUST_BE_FALSE"?"Target":"Member";
        var signals=(p.Source==null?Array.Empty<PipelineSignal>():[new PipelineSignal(p.Source,"Source")]).Concat((p.Members??[]).Select(m=>new PipelineSignal(m,role))).ToArray();
        return new(p.Operator,signals,p.Explanation,p.EvidenceIds,p.Conditions,p.Applicability,p.SettlingTimeMs,p.SettlingTimeSignal,p.ApplicabilityCondition,p.SettlingTimeScale);
    }
    /// <summary>Apply per-candidate votes without allowing an omitted, duplicate or malformed vote to approve a rule.</summary>
    public static IReadOnlyList<BusinessDecision> ApplyReview(BusinessDescription view,IReadOnlyList<BusinessCandidate> candidates,string response) {
        var reviews=JsonSerializer.Deserialize<ReviewResponse>(response,Json)?.Reviews??throw new PipelineException("InvalidTemplateReview");
        var result=new List<BusinessDecision>();
        foreach(var (candidate,index) in candidates.Select((c,i)=>(c,i))) {
            var votes=reviews.Where(v=>v is not null&&v.Index==index).ToArray();
            if(votes.Length!=1||string.IsNullOrWhiteSpace(votes[0].Reason)||votes[0].Replacements is null||votes[0].Replacements.Count>4||votes[0].Accept&&votes[0].Replacements.Count>0) {
                result.Add(new(candidate,"Rejected","MissingOrAmbiguousTemplateReview"));continue;
            }
            var vote=votes[0];var shape=ValidateTemplateCandidate(view,candidate);
            if(vote.Accept) {result.Add(shape.Status=="Validated"?shape with{Code="TemplateModelCodeReviewAccepted:"+vote.Reason}:shape);continue;}
            result.Add(new(candidate,"Rejected","TemplateModelCodeReviewRejected:"+vote.Reason));
            foreach(var replacement in vote.Replacements.Where(p=>p is not null)) {
                var validated=ValidateTemplateCandidate(view,ToCandidate(replacement));
                result.Add(validated.Status=="Validated"?validated with{Code="TemplateModelCodeReviewCorrected:"+vote.Reason}:validated);
            }
        }
        return result;
    }
    static BusinessDecision ValidateTemplateCandidate(BusinessDescription view,BusinessCandidate candidate) {
        var shape=BusinessCandidateValidator.Validate(view,candidate);
        if(shape.Status!="Validated")return shape;
        var symbols=(view.Symbols??[]).Where(s=>s.BlockId==view.BlockId).ToDictionary(s=>s.Alias,StringComparer.Ordinal);
        var booleanUses=candidate.Signals.Select(s=>s.Name).Concat(candidate.Conditions.Select(c=>c.Signal));
        if(candidate.ApplicabilityCondition is not null)booleanUses=booleanUses.Concat(PipelineTerms.Walk(candidate.ApplicabilityCondition).Where(t=>t.Kind=="Signal").Select(t=>t.Name!));
        foreach(var alias in booleanUses) {
            if(symbols.TryGetValue(alias,out var symbol)&&(!symbol.DataType.Equals("Bool",StringComparison.OrdinalIgnoreCase)||symbol.Section is not("Input" or "Output" or "InOut")))
                return new(candidate,"Rejected","NonPublicBooleanTemplateSignal");
        }
        return shape;
    }
    static string NativeType(string type)=>new[]{"Bool","Byte","Word","DWord","LWord","SInt","USInt","Int","UInt","DInt","UDInt","LInt","ULInt","Real","LReal","Time","LTime","String","WString","Char","WChar","Date","DT","TOD"}.Any(t=>t.Equals(type,StringComparison.OrdinalIgnoreCase))?type:"SeeAnonymizedInterface";

    sealed record SliceTerm(string Kind,string Name,SliceTerm[] Children);
    public sealed record MonitorChain(string ActivityOutput,string CompletionOutput,string PendingGate,string ActivityState,string CompletionState,
        string Counter,IReadOnlyList<BusinessNumericFact> CounterFacts,IReadOnlyList<BusinessCondition> Conditions,
        IReadOnlyList<string> CompletionTrueInputs,IReadOnlyList<string> CompletionFalseInputs,IReadOnlyList<string> EvidenceIds,IReadOnlyList<string> OutputReadGates,IReadOnlyList<ResetBehavior> ResetBehaviors,IReadOnlyDictionary<string,int> EdgeMemoryWriters);
    public sealed record ResetBehavior(string Uid,string MemoryBit,int Writers,bool PendingNonErrorInput,string Meaning);

    /// <summary>Recognizes an exact shared Boolean subexpression and follows the actual numeric target's
    /// producers/consumers. This fixes identities and polarity; the model judges the diagnostic expectation.</summary>
    public static IReadOnlyList<MonitorChain> BuildMonitorChains(BusinessDescription description,IReadOnlyList<BusinessNumericFact>? numeric=null) {
        numeric??=BusinessNumericEvidence.Build(description);
        var symbols=(description.Symbols??[]).Where(s=>s.BlockId==description.BlockId).ToArray();
        var publicBools=symbols.Where(s=>s.Section is "Input" or "Output" or "InOut"&&s.DataType.Equals("Bool",StringComparison.OrdinalIgnoreCase)&&!s.Path.Contains('.')&&!s.Path.Contains('[')).Select(s=>s.Alias).ToHashSet(StringComparer.Ordinal);
        var outputs=symbols.Where(s=>s.Section=="Output"&&publicBools.Contains(s.Alias)).Select(s=>s.Alias).ToHashSet(StringComparer.Ordinal);
        using var inventory=JsonDocument.Parse(StableAssignmentSlices(description,outputs.Order().ToArray()));
        if(!inventory.RootElement.TryGetProperty("equationTargets",out var targets))return [];
        var eligible=targets.EnumerateArray().Select(e=>e.GetString()!).ToHashSet(StringComparer.Ordinal);
        var writerCounts=inventory.RootElement.GetProperty("writeCounts").EnumerateArray().ToDictionary(e=>e.GetProperty("name").GetString()!,e=>e.GetProperty("count").GetInt32(),StringComparer.Ordinal);
        var equations=new Dictionary<string,(SliceTerm Term,string Evidence)>(StringComparer.Ordinal);
        foreach(var e in description.Evidence.Where(e=>e.Kind=="ControlFlow")) {
            var match=System.Text.RegularExpressions.Regex.Match(e.Text,@"^(?<n>[PS]\d+) := (?<expr>.+) \(Coil\) \[source");
            if(!match.Success||!eligible.Contains(match.Groups["n"].Value))continue;
            try {equations[match.Groups["n"].Value]=(new SliceParser(match.Groups["expr"].Value).Parse(),e.Id);}catch(FormatException) { }
        }
        string Key(SliceTerm t)=>t.Kind+":"+t.Name+"("+string.Join(",",t.Children.Select(Key))+")";
        HashSet<(string Name,bool Value)> Must(SliceTerm t,bool value=true) {
            if(t.Kind=="Atom")return [(t.Name,value)];if(t.Kind=="Not")return Must(t.Children[0],!value);
            if(t.Kind is not("And" or "Or"))return [];
            var children=t.Children.Where(c=>!(t.Kind=="And"&&c.Kind=="True")&&!(t.Kind=="Or"&&c.Kind=="False")).ToArray();
            if(children.Length==0)return [];var set=Must(children[0],value);
            foreach(var child in children.Skip(1)){if((t.Kind=="And")==value)set.UnionWith(Must(child,value));else set.IntersectWith(Must(child,value));}return set;
        }
        string[]? PositiveConjunction(SliceTerm t) {
            if(t.Kind=="Atom")return [t.Name];if(t.Kind=="True")return [];if(t.Kind!="And")return null;
            var children=t.Children.Select(PositiveConjunction).ToArray();return children.Any(c=>c is null)?null:children.SelectMany(c=>c!).ToArray();
        }
        bool? Value(SliceTerm t,IReadOnlyDictionary<string,bool> known) {
            if(t.Kind=="Atom")return known.TryGetValue(t.Name,out var atom)?atom:null;
            if(t.Kind=="True")return true;if(t.Kind=="False")return false;if(t.Kind=="Unknown")return null;
            if(t.Kind=="Not")return Value(t.Children[0],known) is bool x?!x:null;
            var values=t.Children.Select(c=>Value(c,known)).ToArray();
            return t.Kind=="And"?values.Any(v=>v==false)?false:values.All(v=>v==true)?true:null:values.Any(v=>v==true)?true:values.All(v=>v==false)?false:null;
        }
        string[]? Arguments(string expression,string function) {
            if(!expression.StartsWith(function+"(",StringComparison.Ordinal)||!expression.EndsWith(')'))return null;
            var values=new List<string>();var start=function.Length+1;var depth=0;
            for(var i=start;i<expression.Length-1;i++) {
                if(expression[i]=='(')depth++;else if(expression[i]==')'){if(--depth<0)return null;}
                else if(expression[i]==','&&depth==0){values.Add(expression[start..i].Trim());start=i+1;}
            }
            if(depth!=0)return null;values.Add(expression[start..^1].Trim());return values.ToArray();
        }
        bool ConjunctiveGuard(SliceTerm t,IReadOnlyDictionary<string,bool> known) {
            if(Value(t,known) is bool value)return value;
            if(t.Kind is "Atom" or "Unknown"||t.Kind=="Not"&&t.Children[0].Kind=="Atom")return true;
            return t.Kind=="And"&&t.Children.All(c=>ConjunctiveGuard(c,known));
        }
        bool HasFixedEdgeOperand(string expression,IReadOnlyDictionary<string,bool> known) {
            foreach(System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(expression,@"(?<kind>RISING_EDGE|FALLING_EDGE)\(")) {
                var depth=1;var end=match.Index+match.Length;
                while(end<expression.Length&&depth>0){if(expression[end]=='(')depth++;else if(expression[end]==')')depth--;end++;}
                if(depth!=0)return true;var args=Arguments(expression[match.Index..end],match.Groups["kind"].Value);
                if(args is null||args.Length!=2)return true;
                try {if(Value(new SliceParser(args[0]).Parse(),known) is not null)return true;}catch(FormatException){return true;}
            }
            return false;
        }
        bool KnownThresholdCanAdvance(BusinessNumericFact threshold,double increment,string type) {
            if(!threshold.Inputs.TryGetValue("in2",out var text))return false;
            var literal=System.Text.RegularExpressions.Regex.Match(text,@"^[A-Za-z]+\((?<n>[^()]+)\)$");
            if(!literal.Success)return true; // No claim about arbitrary dynamic ranges; those remain a diagnostic applicability boundary.
            if(!double.TryParse(literal.Groups["n"].Value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var limit)||!double.IsFinite(limit)||limit<=0)return false;
            if(type.Equals("Real",StringComparison.OrdinalIgnoreCase)) {
                var nativeLimit=(float)limit;var nativeStep=(float)increment;var boundary=threshold.Instruction=="Gt"?nativeLimit:MathF.BitDecrement(nativeLimit);var next=boundary+nativeStep;
                // The largest relevant ULP bounds every nonnegative value below the threshold (GE)
                // or through the threshold (GT). Strictly exceed half-ULP to exclude even/odd ties.
                var largestUlp=(double)MathF.BitIncrement(boundary)-boundary;
                return float.IsFinite(nativeLimit)&&nativeLimit>0&&float.IsFinite(nativeStep)&&nativeStep>0&&float.IsFinite(next)&&nativeStep>largestUlp/2;
            }
            if(type.Equals("LReal",StringComparison.OrdinalIgnoreCase)){var boundary=threshold.Instruction=="Gt"?limit:Math.BitDecrement(limit);var largestUlp=Math.BitIncrement(boundary)-boundary;return double.IsFinite(boundary+increment)&&increment>largestUlp/2;}
            return true;
        }
        (string Output,string[] Gates,string Evidence)[] Maps(string state)=>equations.Where(e=>outputs.Contains(e.Key)).Select(e=>(e.Key,Atoms:PositiveConjunction(e.Value.Term),e.Value.Evidence))
            .Where(e=>e.Atoms is not null&&e.Atoms.Count(a=>a==state)==1).Select(e=>(e.Key,e.Atoms!.Where(a=>a!=state).Order(StringComparer.Ordinal).ToArray(),e.Evidence)).ToArray();
        var rows=inventory.RootElement.GetProperty("outputs").EnumerateArray().ToDictionary(r=>r.GetProperty("sourceTrue").GetString()!,r=>r);
        var result=new List<MonitorChain>();
        foreach(var add in numeric.Where(f=>f.Instruction=="Add")) {
            var destinations=add.Outputs.Where(o=>o.WriteKind=="EnabledNumericWrite"&&System.Text.RegularExpressions.Regex.IsMatch(o.Target,@"^P\d+$")).Select(o=>o.Target).Distinct().ToArray();
            if(destinations.Length!=1||add.Inputs.Count!=2)continue;var counter=destinations[0];
            if(add.Inputs.Values.Count(v=>v==counter)!=1)continue;var step=add.Inputs.Values.Single(v=>v!=counter);
            var number=System.Text.RegularExpressions.Regex.Match(step,@"^[A-Za-z]+\((?<n>[+-]?[0-9]+(?:\.[0-9]+)?)\)$");
            if(!number.Success||!double.TryParse(number.Groups["n"].Value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var increment)||!double.IsFinite(increment)||increment<=0)continue;
            if(add.DataType.Equals("Real",StringComparison.OrdinalIgnoreCase)&&(!float.IsFinite((float)increment)||(float)increment<=0))continue;
            var thresholds=numeric.Where(f=>f.BlockRef==add.BlockRef&&f.DataType.Equals(add.DataType,StringComparison.OrdinalIgnoreCase)&&f.Instruction is "Ge" or "Gt"&&f.Inputs.TryGetValue("in1",out var n)&&n==counter&&f.Outputs.Any(o=>o.WriteKind!="Dataflow"&&eligible.Contains(o.Target))).ToArray();
            if(thresholds.Length==0)continue;
            HashSet<(string Name,bool Value)> required;
            if(add.Enable.Contains("Unknown(",StringComparison.Ordinal)||thresholds.Any(f=>f.Precondition.Contains("Unknown(",StringComparison.Ordinal)))continue;
            try {
                required=Must(new SliceParser(add.Enable).Parse());
                foreach(var threshold in thresholds.Where(f=>f.Precondition is not("NotPresent" or "NotApplicable")))required.UnionWith(Must(new SliceParser(threshold.Precondition).Parse()));
            }catch(FormatException){continue;}
            foreach(var gate in required.Where(p=>p.Value&&equations.ContainsKey(p.Name))) {
                var eq=equations[gate.Name];if(eq.Term.Kind!="And"||eq.Term.Children.Length!=2)continue;
                foreach(var negated in eq.Term.Children.Where(c=>c.Kind=="Not"&&c.Children[0].Kind=="Atom")) {
                    var completion=negated.Children[0].Name;
                    if(!equations.ContainsKey(completion))continue;
                    var completionFalse=Must(equations[completion].Term).Where(p=>!p.Value).Select(p=>p.Name).ToHashSet(StringComparer.Ordinal);
                    var inhibitingThresholds=thresholds.Where(f=>f.Outputs.Any(o=>o.WriteKind!="Dataflow"&&completionFalse.Contains(o.Target))).ToArray();
                    if(inhibitingThresholds.Length==0||inhibitingThresholds.Any(f=>!KnownThresholdCanAdvance(f,increment,add.DataType)))continue;
                    var activeExpression=eq.Term.Children.Single(c=>!ReferenceEquals(c,negated));
                    foreach(var activity in equations.Where(e=>e.Key!=gate.Name&&Key(e.Value.Term)==Key(activeExpression)))
                    foreach(var sourceMap in Maps(activity.Key))foreach(var completionMap in Maps(completion)) {
                        if(sourceMap.Output==completionMap.Output||!sourceMap.Gates.SequenceEqual(completionMap.Gates)||!rows.TryGetValue(sourceMap.Output,out var sourceRow)||!rows.TryGetValue(completionMap.Output,out var completionRow))continue;
                        var knownTrue=sourceRow.GetProperty("requiredTrue").EnumerateArray().Select(v=>v.GetString()!).Append(sourceMap.Output).ToHashSet(StringComparer.Ordinal);
                        var knownFalse=sourceRow.GetProperty("requiredFalse").EnumerateArray().Select(v=>v.GetString()!).ToHashSet(StringComparer.Ordinal);
                        var guards=new List<BusinessCondition>();var usable=true;
                        foreach(var condition in required.Where(c=>c.Name!=gate.Name)) {
                            if((condition.Value?knownTrue:knownFalse).Contains(condition.Name))continue;
                            if(!publicBools.Contains(condition.Name)&&!description.SignalAliases.ContainsKey(condition.Name)){usable=false;break;}
                            guards.Add(new(condition.Name,condition.Value));
                        }
                        if(!usable||guards.GroupBy(g=>g.Signal).Any(g=>g.Select(c=>c.Value).Distinct().Count()>1))continue;
                        var counterFacts=numeric.Where(f=>f.BlockRef==add.BlockRef&&(f.Outputs.Any(o=>o.Target==counter)||f.Inputs.Values.Contains(counter))).ToArray();
                        if(counterFacts.Any(f=>f.Outputs.Any(o=>o.Target==counter)&&f.Enable.Contains("Unknown(",StringComparison.Ordinal)))continue;
                        var edgeMemories=counterFacts.SelectMany(f=>System.Text.RegularExpressions.Regex.Matches(f.Enable+" "+f.Precondition,@"PREVIOUS\((?<n>[PS]\d+)\)").Select(m=>m.Groups["n"].Value)).Distinct()
                            .ToDictionary(bit=>bit,bit=>writerCounts.GetValueOrDefault(bit),StringComparer.Ordinal);
                        if(edgeMemories.Values.Any(count=>count!=1))continue;
                        var phase=knownTrue.ToDictionary(a=>a,_=>true,StringComparer.Ordinal);
                        foreach(var a in knownFalse)phase[a]=false;
                        void SetPhase(string name,bool value){if(phase.TryGetValue(name,out var old)&&old!=value)usable=false;else phase[name]=value;}
                        SetPhase(gate.Name,true);SetPhase(completion,false);
                        foreach(var condition in guards)SetPhase(condition.Signal,condition.Value);
                        foreach(var alarm in inhibitingThresholds.SelectMany(f=>f.Outputs).Where(o=>o.WriteKind!="Dataflow"&&eligible.Contains(o.Target)))SetPhase(alarm.Target,false);
                        if(!usable)continue;
                        try {
                            if(!ConjunctiveGuard(new SliceParser(add.Enable).Parse(),phase)||HasFixedEdgeOperand(add.Enable,phase)||
                                inhibitingThresholds.Where(f=>f.Precondition is not("NotPresent" or "NotApplicable")).Any(f=>!ConjunctiveGuard(new SliceParser(f.Precondition).Parse(),phase)))continue;
                        }catch(FormatException){continue;}
                        var resets=new List<ResetBehavior>();
                        foreach(var reset in counterFacts.Where(f=>f.Instruction=="Move"&&f.Outputs.Any(o=>o.Target==counter))) {
                            var edge=Arguments(reset.Enable,"RISING_EDGE");
                            var memory=edge?.Length==2?Arguments(edge[1],"PREVIOUS"):null;
                            var zero=reset.Inputs.TryGetValue("in",out var input)&&System.Text.RegularExpressions.Regex.IsMatch(input,@"^[A-Za-z]+\([+-]?0(?:\.0+)?\)$");
                            if(!zero||edge is null||edge.Length!=2||memory is null||memory.Length!=1||writerCounts.GetValueOrDefault(memory[0])!=1){usable=false;break;}
                            bool? inputLevel;
                            try {inputLevel=Value(new SliceParser(edge[0]).Parse(),phase);}catch(FormatException){usable=false;break;}
                            if(inputLevel is null){usable=false;break;}
                            resets.Add(new(reset.Uid,memory[0],1,inputLevel.Value,"Dedicated edge memory. In stable pending/non-error phase the edge input is constant; after its first normal evaluation it cannot repeatedly trigger Move. Increment and reset may still coincide on the first transition scan; their mutual exclusion is NOT asserted. Error/reset-input transitions remain governed by the full EN expression."));
                        }
                        if(!usable)continue;
                        var trueInputs=completionRow.GetProperty("requiredTrue").EnumerateArray().Select(v=>v.GetString()!).Where(a=>publicBools.Contains(a)&&!outputs.Contains(a)&&!knownTrue.Contains(a)).ToArray();
                        var falseInputs=completionRow.GetProperty("requiredFalse").EnumerateArray().Select(v=>v.GetString()!).Where(a=>publicBools.Contains(a)&&!outputs.Contains(a)&&!knownFalse.Contains(a)).ToArray();
                        var evidence=counterFacts.Select(f=>f.EvidenceId).Concat(new[]{eq.Evidence,activity.Value.Evidence,equations[completion].Evidence,sourceMap.Evidence,completionMap.Evidence})
                            .Concat(completionRow.GetProperty("evidenceIds").EnumerateArray().Select(e=>e.GetString()!)).Distinct().ToArray();
                        result.Add(new(sourceMap.Output,completionMap.Output,gate.Name,activity.Key,completion,counter,counterFacts,guards,trueInputs,falseInputs,evidence,sourceMap.Gates,resets,edgeMemories));
                    }
                }
            }
        }
        return result.DistinctBy(c=>c.ActivityOutput+"|"+c.CompletionOutput+"|"+c.Counter+"|"+JsonSerializer.Serialize(c.Conditions)).ToArray();
    }
    public static IReadOnlyList<BusinessCandidate> StableSliceCandidates(BusinessDescription description) {
        var outputs=(description.Symbols??[]).Where(s=>s.BlockId==description.BlockId&&s.Section=="Output"&&s.DataType.Equals("Bool",StringComparison.OrdinalIgnoreCase)&&!s.Path.Contains('.')&&!s.Path.Contains('[')).Select(s=>s.Alias).ToHashSet(StringComparer.Ordinal);
        using var slices=JsonDocument.Parse(StableAssignmentSlices(description,outputs.Order().ToArray(),publicOnly:true));
        var rules=new List<BusinessCandidate>();
        foreach(var row in slices.RootElement.GetProperty("outputs").EnumerateArray()) {
            var source=row.GetProperty("sourceTrue").GetString()!;
            var evidence=row.GetProperty("evidenceIds").EnumerateArray().Select(e=>e.GetString()!).ToArray();
            if(evidence.Length==0)continue;
            var positive=row.GetProperty("requiredTrue").EnumerateArray().Select(e=>e.GetString()!).ToArray();
            var negative=row.GetProperty("requiredFalse").EnumerateArray().Select(e=>e.GetString()!).ToArray();
            if(positive.Length>0)rules.Add(new("IMPLIES",new[]{new PipelineSignal(source,"Source")}.Concat(positive.Select(p=>new PipelineSignal(p,"Target"))).ToArray(),
                "在所有相关值跨网络及前一扫描已稳定的前提下，由单写普通线圈等式布尔传播得到必要真值；不适用于瞬时变化。",evidence,[],"StableDiagnosticSnapshot"));
            var negativeInputs=negative.Where(n=>!outputs.Contains(n)).ToArray();
            if(negativeInputs.Length>0)rules.Add(new("REQUIRES_0",new[]{new PipelineSignal(source,"Source")}.Concat(negativeInputs.Select(p=>new PipelineSignal(p,"Target"))).ToArray(),
                "在所有相关值跨网络及前一扫描已稳定的前提下，由完整AND/OR/NOT等式传播得到必要假值；OR分支未被省略。",evidence,[],"StableDiagnosticSnapshot"));
            foreach(var other in negative.Where(outputs.Contains))rules.Add(new("AT_MOST_ONE",new[]{source,other}.Order(StringComparer.Ordinal).Select(p=>new PipelineSignal(p,"Member")).ToArray(),
                "稳定快照下一个公开输出为真必使另一公开输出为假；来自单写入布尔等式传播，不推断物理互斥。",evidence,[],"StableDiagnosticSnapshot"));
        }
        return rules.DistinctBy(BusinessCandidateValidator.Key).ToArray();
    }
    /// <summary>Conservative Boolean propagation over single-write ordinary-coil equations under an explicit
    /// fixed-point snapshot assumption. Produces evidence facts, never diagnostic candidates.</summary>
    public static string StableAssignmentSlices(BusinessDescription description,IReadOnlyList<string> outputs,bool publicOnly=false) {
        var facts=description.Evidence.Where(e=>e.Kind=="ControlFlow").ToArray();
        var writes=new Dictionary<string,int>(StringComparer.Ordinal);
        var equations=new List<(string Name,SliceTerm Term,string Evidence)>();
        foreach(var fact in facts) {
            var target=System.Text.RegularExpressions.Regex.Match(fact.Text,@"^(?<n>[PS]\d+) := |(?:SET|RESET) (?<n>[PS]\d+)");
            if(!target.Success)continue;var name=target.Groups["n"].Value;writes[name]=writes.GetValueOrDefault(name)+1;
            if(!description.ModelText.Contains("["+fact.Id+"]",StringComparison.Ordinal))continue;
            var match=System.Text.RegularExpressions.Regex.Match(fact.Text,@"^[PS]\d+ := (?<expr>.+) \(Coil\) \[source");
            if(!match.Success)continue;
            try {var parser=new SliceParser(match.Groups["expr"].Value);equations.Add((name,parser.Parse(),fact.Id));}catch(FormatException) { }
        }
        var otherTargets=new HashSet<string>(StringComparer.Ordinal);
        var otherWriteCounts=new Dictionary<string,int>(StringComparer.Ordinal);
        var rawCoilWrites=new Dictionary<string,int>(StringComparer.Ordinal);
        foreach(var source in description.Evidence.Where(e=>e.Kind=="SourceStructure")) {
            var start=source.Text.IndexOf('<');var end=source.Text.LastIndexOf('>');
            if(start<0||end<start)return JsonSerializer.Serialize(new{scope="UnknownWriterInventory",outputs=Array.Empty<object>()},Json);
            try {
                using var reader=System.Xml.XmlReader.Create(new System.IO.StringReader(source.Text[start..(end+1)]),new System.Xml.XmlReaderSettings{DtdProcessing=System.Xml.DtdProcessing.Prohibit,XmlResolver=null});
                var xml=System.Xml.Linq.XElement.Load(reader);
                if(xml.Name.LocalName!="FlgNet")return JsonSerializer.Serialize(new{scope="UnsupportedWriterLanguage",outputs=Array.Empty<object>()},Json);
                // A called block may modify hidden instance memory. No interprocedural assumptions here.
                if(xml.Descendants().Any(e=>e.Name.LocalName=="Call"))return JsonSerializer.Serialize(new{scope="OpaqueInterproceduralWritesNotAnalyzed",outputs=Array.Empty<object>()},Json);
                var accesses=xml.Descendants().Where(e=>e.Name.LocalName=="Access").ToDictionary(e=>(string?)e.Attribute("UId")??"",e=>(string?)e.Attribute("SymbolRef")??"");
                var parts=xml.Descendants().Where(e=>e.Name.LocalName=="Part").ToDictionary(e=>(string?)e.Attribute("UId")??"",e=>(string?)e.Attribute("Name")??"");
                var understoodParts=new HashSet<string>(["Contact","Coil","SCoil","RCoil","A","AND","And","O","OR","Or","NOT","Not","Add","Sub","Mul","Div","Move","Ge","Gt","Le","Lt","Eq","Ne","PContact","NContact","PBox","NBox"],StringComparer.Ordinal);
                if(parts.Values.Any(kind=>!understoodParts.Contains(kind)))return JsonSerializer.Serialize(new{scope="UnknownInstructionWriterInventory",outputs=Array.Empty<object>()},Json);
                foreach(var wire in xml.Descendants().Where(e=>e.Name.LocalName=="Wire")) {
                    foreach(var pin in wire.Elements().Where(e=>e.Name.LocalName=="NameCon")) {
                        if(!parts.TryGetValue((string?)pin.Attribute("UId")??"",out var kind))continue;
                        var name=(string?)pin.Attribute("Name")??"";
                        var coilWrite=kind is "Coil" or "SCoil" or "RCoil"&&name=="operand";
                        var edgeWrite=kind is "PContact" or "NContact" or "PBox" or "NBox"&&name=="bit";
                        var valueWrite=kind is not("Coil" or "SCoil" or "RCoil")&&(name.StartsWith("out",StringComparison.Ordinal)||name is "Q" or "ET" or "eno");
                        if(!coilWrite&&!edgeWrite&&!valueWrite)continue;
                        foreach(var endpoint in wire.Elements().Where(e=>e.Name.LocalName=="IdentCon")) {
                            if(!accesses.TryGetValue((string?)endpoint.Attribute("UId")??"",out var alias)||alias.Length==0)
                                return JsonSerializer.Serialize(new{scope="UnresolvedWriteTarget",outputs=Array.Empty<object>()},Json);
                            if(coilWrite)rawCoilWrites[alias]=rawCoilWrites.GetValueOrDefault(alias)+1;
                            else {otherTargets.Add(alias);otherWriteCounts[alias]=otherWriteCounts.GetValueOrDefault(alias)+1;}
                        }
                    }
                }
            }catch(Exception ex)when(ex is System.Xml.XmlException or ArgumentException) {return JsonSerializer.Serialize(new{scope="UnresolvedWriterInventory",outputs=Array.Empty<object>()},Json);}
        }
        foreach(var write in rawCoilWrites)writes[write.Key]=Math.Max(writes.GetValueOrDefault(write.Key),write.Value);
        var rootSymbols=(description.Symbols??[]).Where(s=>s.BlockId==description.BlockId).ToArray();
        // Without a caller-specific physical alias map, a global write may alias local/instance memory.
        if(writes.Keys.Concat(otherTargets).Any(alias=>alias.StartsWith("S",StringComparison.Ordinal)))
            return JsonSerializer.Serialize(new{scope="GlobalWriterAliasNotProvedDisjoint",outputs=Array.Empty<object>()},Json);
        var writtenAliases=writes.Keys.Concat(otherTargets).ToHashSet(StringComparer.Ordinal);
        foreach(var symbol in rootSymbols.Where(s=>writtenAliases.Contains(s.Alias))) {
            var indices=System.Text.RegularExpressions.Regex.Matches(symbol.Path,@"\[(?<index>[^\]]*)\]");
            if(indices.Any(m=>m.Groups["index"].Value.Split(',').Any(index=>
                !long.TryParse(index.Trim(),System.Globalization.NumberStyles.Integer,System.Globalization.CultureInfo.InvariantCulture,out var value)||
                index.Trim()!=value.ToString(System.Globalization.CultureInfo.InvariantCulture))))
                return JsonSerializer.Serialize(new{scope="DynamicWriterAliasNotResolved",outputs=Array.Empty<object>()},Json);
        }
        var publicAliases=rootSymbols.Where(s=>s.Section is "Input" or "Output" or "InOut"&&s.DataType.Equals("Bool",StringComparison.OrdinalIgnoreCase))
            .Select(s=>s.Alias).Concat(description.SignalAliases.Keys).ToHashSet(StringComparer.Ordinal);
        foreach(var write in rootSymbols.Where(s=>otherTargets.Contains(s.Alias)).ToArray())
            foreach(var child in rootSymbols.Where(s=>s.Path.StartsWith(write.Path+".",StringComparison.Ordinal)||s.Path.StartsWith(write.Path+"[",StringComparison.Ordinal)))otherTargets.Add(child.Alias);
        equations=equations.Where(e=>writes[e.Name]==1&&!otherTargets.Contains(e.Name)).Take(256).ToList();
        var result=new List<object>();
        foreach(var output in outputs.Take(64)) {
            var known=new Dictionary<string,bool>(StringComparer.Ordinal){{output,true}};var used=new HashSet<string>(StringComparer.Ordinal);
            bool conflict=false,changed=false;int operations=0;
            void Assign(string name,bool value,string evidence) {
                if(known.TryGetValue(name,out var previous)){if(previous!=value)conflict=true;return;}
                known[name]=value;used.Add(evidence);changed=true;
            }
            bool? Evaluate(SliceTerm term) {
                if(++operations>200000)throw new InvalidOperationException("SliceBudget");
                if(term.Kind=="Atom")return known.TryGetValue(term.Name,out var atomValue)?atomValue:null;
                if(term.Kind=="True")return true;if(term.Kind=="False")return false;
                if(term.Kind=="Unknown")return null;
                if(term.Kind=="Not")return Evaluate(term.Children[0]) is bool notValue?!notValue:null;
                var values=term.Children.Select(Evaluate).ToArray();
                if(term.Kind=="And")return values.Any(v=>v==false)?false:values.All(v=>v==true)?true:null;
                return values.Any(v=>v==true)?true:values.All(v=>v==false)?false:null;
            }
            void Require(SliceTerm term,bool value,string evidence) {
                if(term.Kind=="Unknown")return;
                if(term.Kind=="Atom"){Assign(term.Name,value,evidence);return;}
                if(term.Kind=="True"||term.Kind=="False"){if((term.Kind=="True")!=value)conflict=true;return;}
                if(term.Kind=="Not"){Require(term.Children[0],!value,evidence);return;}
                if((term.Kind=="And")==value){foreach(var child in term.Children)Require(child,value,evidence);return;}
                var children=term.Children.Select(c=>(Term:c,Value:Evaluate(c))).ToArray();
                if(children.Any(c=>c.Value==value))return;
                var unknown=children.Where(c=>c.Value is null).ToArray();
                if(unknown.Length==0)conflict=true;else if(unknown.Length==1)Require(unknown[0].Term,value,evidence);
            }
            try {
                for(var iteration=0;iteration<256&&!conflict;iteration++) {
                    changed=false;
                    foreach(var eq in equations) {
                        if(Evaluate(eq.Term) is bool value)Assign(eq.Name,value,eq.Evidence);
                        if(known.TryGetValue(eq.Name,out var expected)){used.Add(eq.Evidence);Require(eq.Term,expected,eq.Evidence);}
                        if(conflict)break;
                    }
                    if(!changed)break;
                }
            }catch(InvalidOperationException){continue;}
            // An impossible premise must not create vacuously true contracts.
            if(conflict)continue;
            result.Add(new{sourceTrue=output,requiredTrue=known.Where(p=>p.Value&&p.Key!=output&&(!publicOnly||publicAliases.Contains(p.Key))).Select(p=>p.Key).Order().ToArray(),
                requiredFalse=known.Where(p=>!p.Value&&(!publicOnly||publicAliases.Contains(p.Key))).Select(p=>p.Key).Order().ToArray(),evidenceIds=used.Order().ToArray()});
        }
        var writeCounts=writes.Keys.Concat(otherWriteCounts.Keys).Distinct().Select(name=>new{name,count=writes.GetValueOrDefault(name)+otherWriteCounts.GetValueOrDefault(name)}).ToArray();
        return JsonSerializer.Serialize(new{scope="StableDiagnosticSnapshot ONLY: all involved values fixed across the ordered networks and preceding scan. Not a transient program invariant. Unknown and multiple-write equations are not expanded.",equationTargets=equations.Select(e=>e.Name).ToArray(),writeCounts,outputs=result},Json);
    }
    sealed class SliceParser(string text) {
        int position,nodes;
        void Space(){while(position<text.Length&&char.IsWhiteSpace(text[position]))position++;}
        public SliceTerm Parse(){var value=Read();Space();if(position!=text.Length)throw new FormatException();return value;}
        SliceTerm Read() {
            if(++nodes>1024)throw new FormatException();Space();var start=position;
            while(position<text.Length&&(char.IsLetterOrDigit(text[position])||text[position]=='_'))position++;
            if(position==start)throw new FormatException();var name=text[start..position];Space();
            if(position==text.Length||text[position]!='(')return name switch {
                "TRUE"=>new("True","",[]),"FALSE"=>new("False","",[]),
                _ when System.Text.RegularExpressions.Regex.IsMatch(name,@"^[PS]\d+$")=>new("Atom",name,[]),_=>throw new FormatException()};
            position++;
            if(name.Equals("Bool",StringComparison.OrdinalIgnoreCase)) {
                var close=text.IndexOf(')',position);
                if(close>=position&&bool.TryParse(text[position..close].Trim(),out var literal)){position=close+1;return new(literal?"True":"False","",[]);}
            }
            if(name is not("AND" or "OR" or "NOT")) {
                var depth=1;while(position<text.Length&&depth>0){if(text[position]=='(')depth++;if(text[position]==')')depth--;position++;}
                if(depth!=0)throw new FormatException();return new("Unknown","",[]);
            }
            var children=new List<SliceTerm>();
            while(true){children.Add(Read());Space();if(position>=text.Length)throw new FormatException();var next=text[position++];if(next==')')break;if(next!=',')throw new FormatException();}
            if(name=="NOT"&&children.Count!=1)throw new FormatException();
            return new(name=="AND"?"And":name=="OR"?"Or":"Not","",children.ToArray());
        }
    }
}
