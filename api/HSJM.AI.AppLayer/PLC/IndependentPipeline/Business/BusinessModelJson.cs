#nullable enable
using System;
using System.Linq;
using System.Text.Json;
namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

/// <summary>Accept an explicit collection envelope without altering any candidate or review value.</summary>
public static class BusinessModelJson
{
    public static string Array(string text,string wrapper) {
        using var document=JsonDocument.Parse(text);
        var root=document.RootElement;
        if(root.ValueKind==JsonValueKind.Array)return root.GetRawText();
        if(root.ValueKind==JsonValueKind.Object) {
            var properties=root.EnumerateObject().ToArray();
            if(properties.Length==1&&properties[0].Name.Equals(wrapper,StringComparison.OrdinalIgnoreCase)&&properties[0].Value.ValueKind==JsonValueKind.Array)
                return properties[0].Value.GetRawText();
        }
        throw new JsonException("Expected an array or its single explicit "+wrapper+" envelope");
    }
}
