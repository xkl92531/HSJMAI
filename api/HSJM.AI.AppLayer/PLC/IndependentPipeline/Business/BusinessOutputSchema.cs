#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

/// <summary>Constrained output vocabulary for small models; semantic validation remains separate.</summary>
public static class BusinessOutputSchema
{
    public static JsonObject UnitReview(int count)=>new() { ["type"]="json_schema",["json_schema"]=new JsonObject {
        ["name"]="unit_review",["strict"]=true,["schema"]=Obj(("reviews",new JsonObject {
            ["type"]="array",["minItems"]=count,["maxItems"]=count,["items"]=Obj(
                ("index",new JsonObject{{"type","integer"},{"minimum",0},{"maximum",Math.Max(0,count-1)}}),
                ("accept",new JsonObject{{"type","boolean"}}),("reason",new JsonObject{{"type","string"},{"minLength",1},{"maxLength",600}}))
        }))} };
    public static JsonObject MonitorJudgement()=>new() { ["type"]="json_schema",["json_schema"]=new JsonObject {
        ["name"]="monitor_judgement",["strict"]=true,["schema"]=Obj(("supported",new JsonObject{{"type","boolean"}}),
            ("reason",new JsonObject{{"type","string"},{"minLength",1},{"maxLength",800}}),
            ("limitations",new JsonObject{{"type","array"},{"items",Text()},{"maxItems",6}}))} };
    static JsonObject Text(IEnumerable<string>? values=null)=>values is null?new(){{"type","string"}}:
        new(){{"type","string"},{"enum",new JsonArray(values.Distinct(StringComparer.Ordinal).Select(v=>(JsonNode?)JsonValue.Create(v)).ToArray())}};
    static JsonObject Obj(params (string Name,JsonNode Schema)[] fields)=>new() {
        ["type"]="object",["additionalProperties"]=false,
        ["properties"]=new JsonObject(fields.Select(f=>new KeyValuePair<string,JsonNode?>(f.Name,f.Schema))),
        ["required"]=new JsonArray(fields.Select(f=>(JsonNode?)JsonValue.Create(f.Name)).ToArray())
    };
    public static JsonObject Template(IReadOnlyList<string> boolSignals,IReadOnlyList<string> allSignals,IReadOnlyList<string> evidenceIds) {
        var condition=Obj(("signal",Text(boolSignals)),("value",new JsonObject{{"type","boolean"}}));
        var nullableSignal=Text(allSignals);nullableSignal["type"]=new JsonArray("string","null");nullableSignal["enum"]!.AsArray().Add(null);
        var nullableSource=Text(boolSignals);nullableSource["type"]=new JsonArray("string","null");nullableSource["enum"]!.AsArray().Add(null);
        var candidate=Obj(
            ("explanation",Text()),("evidenceIds",new JsonObject{{"type","array"},{"items",Text(evidenceIds)},{"minItems",1},{"maxItems",16}}),
            ("conditions",new JsonObject{{"type","array"},{"items",condition},{"maxItems",8}}),
            ("applicability",Text(["ProgramRelation","NormalOperation","StableDiagnosticSnapshot","AfterSettling"])),
            ("settlingTimeMs",new JsonObject{{"type",new JsonArray("integer","null")},{"minimum",0}}),
            ("settlingTimeSignal",nullableSignal),("settlingTimeScale",new JsonObject{{"type","number"},{"enum",new JsonArray(1)}}),
            ("applicabilityCondition",new JsonObject{{"type","null"}}),
            ("operator",Text(["AT_MOST_ONE","EXACTLY_ONE","SAME_STATE","IMPLIES","REQUIRES_0","ALL_TRUE","MUST_BE_FALSE"])),
            ("source",nullableSource),("members",new JsonObject{{"type","array"},{"items",Text(boolSignals)},{"minItems",1},{"maxItems",32}}));
        var schema=Obj(("summary",Text()),("candidates",new JsonObject{{"type","array"},{"items",candidate},{"maxItems",24}}));
        return new() { ["type"]="json_schema",["json_schema"]=new JsonObject{["name"]="plc_block_contract",["strict"]=true,["schema"]=schema} };
    }
    public static JsonObject TemplateReview(IReadOnlyList<string> boolSignals,IReadOnlyList<string> allSignals,IReadOnlyList<string> evidenceIds) {
        var proposal=Template(boolSignals,allSignals,evidenceIds)["json_schema"]!["schema"]!["properties"]!["candidates"]!["items"]!.DeepClone();
        var item=Obj(("reason",Text()),("index",new JsonObject{{"type","integer"},{"minimum",0}}),
            ("accept",new JsonObject{{"type","boolean"}}),
            ("replacements",new JsonObject{{"type","array"},{"items",proposal},{"maxItems",4}}));
        return new() { ["type"]="json_schema",["json_schema"]=new JsonObject{["name"]="plc_contract_review",["strict"]=true,
            ["schema"]=Obj(("reviews",new JsonObject{{"type","array"},{"items",item},{"maxItems",128}}))} };
    }
}
