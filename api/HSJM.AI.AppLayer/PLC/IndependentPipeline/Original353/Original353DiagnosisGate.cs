using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

/// <summary>The old diagnosis evaluator only knows one Boolean frame. It must not turn
/// a version-two temporal or unresolved rule into an immediate physical fault.</summary>
public static class Original353DiagnosisGate
{
    public static bool CanEvaluateInstantly(PLC_IOConstraintRuleEntity rule)
        => rule != null && rule.Source != "XMLV2_TIMED" && rule.Source != "XMLV2_REVIEW" && rule.Source != "XMLV2_MODE";
}
