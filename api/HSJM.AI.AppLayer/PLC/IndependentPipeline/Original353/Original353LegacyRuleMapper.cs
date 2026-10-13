#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

public sealed record Original353LegacyRuleRows(PLC_IOConstraintRuleEntity Rule,
    IReadOnlyList<PLC_IOConstraintSignalEntity> Signals);

/// <summary>Preserve every recognized direct rule in the test database. Source encodes whether
/// the legacy one-frame evaluator may use it; delayed/unknown rules remain visible but inert.</summary>
public static class Original353LegacyRuleMapper
{
    public static Original353LegacyRuleRows Map(long projectId, long logicUnitId,
        PipelineRule rule, IReadOnlyList<PipelineTag> tags, bool persistSourceLuId = true,
        Original353RuleWording? wording = null,
        IReadOnlyDictionary<string, string?>? tagComments = null,
        IReadOnlyList<Original353ManualModeBinding>? modeBindings = null,
        string? generatorVersion = null, string? blockName = null)
    {
        if (projectId <= 0 || logicUnitId <= 0 || rule is null || tags is null)
            throw new ArgumentException("Valid project, logic unit, rule and tags required");
        rule = Original353RulePresentation.WithLabel(rule, tags, tagComments);
        var decision = Original353ActivationPolicy.Classify(rule, tags);
        var source = decision.Status switch {
            "ModeCheck" => "XMLV2_MODE",
            "Active" => "XMLV2",
            "RequiresRuntimeGuard" => "XMLV2_TIMED",
            _ => "XMLV2_REVIEW"
        };
        var canonical = Original353RuleText.Render(rule, decision, tags);
        var display = wording is not null && Original353RuleText.IsValidAiText(rule, wording.Name, wording.Description)
            ? wording : canonical;
        var entity = new PLC_IOConstraintRuleEntity {
            ProjectId = projectId, SourceLuId = persistSourceLuId ? logicUnitId : null, Source = source,
            RuleName = canonical.Name,
            Description = TruncateDescription(InlineComments(display.Description, rule, tagComments)),
            Operator = rule.Operator,
            RuleType = rule.Operator switch {
                "AT_MOST_ONE" => "Mutex", "EXACTLY_ONE" => "ExactlyOne",
                "ALL_TRUE" => "AllRequired", "IMPLIES" or "IMPLIES_ANY" => "Pairing",
                "REQUIRES_0" or "MUST_BE_FALSE" or "REQUIRES_0_WHEN_FALSE" => "Safety", _ => "Custom"
            },
            Priority = "Normal", Confidence = null, CreateTime = DateTime.Now
        };
        entity.ValidStates = JsonSerializer.Serialize(new {
            // 发布器传入本次生成结果的版本（关掉名字规则族时带 |namerules-off:<族…>，R1）；不传时为常量版本
            Generator = generatorVersion ?? OriginalConstraintWorkflow.Version,
            LogicUnitId = logicUnitId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            // 归档单元所在块名（2026-10-02 起写入）：按范围重跑与缺程序体保留按它认规则属于哪个块
            BlockName = string.IsNullOrWhiteSpace(blockName) ? null : blockName.Trim(),
            RuleKey = rule.Key,
            RawGeneratorDescription = rule.Description,
            TextOrigin = display.Origin,
            PlainLanguage = display.PlainLanguage,
            ManualModeBindings = modeBindings ?? [],
            ModeConflictSignals = Original353ModeConflictRules.IsModeRule(rule) ? rule.Signals : null,
            rule.Applicability,
            rule.RequiresSettlingEvidence,
            rule.BypassSignals,
            rule.MaskedFeedbackInputs,
            rule.DisplayLabel,
            rule.Proof,
            rule.UpstreamSignals,
            rule.TargetGroups,
            rule.Trace,
            ActivationStatus = decision.Status,
            ActivationReason = decision.Reason
        });
        var rows = rule.Signals.Select((signal, index) => {
            var matches = tags.Where(tag => string.Equals(tag.Name, signal.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            return new PLC_IOConstraintSignalEntity {
                RuleId = entity.Id, SignalName = signal.Name, SignalRole = signal.Role,
                SignalAddress = matches.Length == 1 ? matches[0].Address : null,
                SortOrder = index, CreateTime = entity.CreateTime
            };
        }).ToArray();
        return new(entity, rows);
    }

    /// <summary>入库说明上限（不论列是 text 还是 varchar(500)，与 CLAUDE.md §5.6 一致）：超过 480 字截到 480 字并追加「…」。</summary>
    public const int DescriptionMaxChars = 480;

    public static string TruncateDescription(string description)
    {
        if (description.Length <= DescriptionMaxChars) return description;
        var cut = DescriptionMaxChars;
        // 不把代理对拆成半个字符
        if (char.IsHighSurrogate(description[cut - 1])) cut--;
        return description[..cut] + "…";
    }

    private static string InlineComments(string description, PipelineRule rule,
        IReadOnlyDictionary<string, string?>? comments)
    {
        var names = rule.Signals.Select(signal => signal.Name)
            .Distinct(StringComparer.Ordinal).OrderByDescending(name => name.Length).ToArray();
        if (names.Length == 0) return description;
        var pattern = @"(?<![A-Za-z0-9_])(?:" + string.Join("|", names.Select(Regex.Escape))
            + @")(?![A-Za-z0-9_])";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return Regex.Replace(description, pattern, match => {
            var name = match.Value;
            if (!seen.Add(name)) return name;
            string? rawComment = null;
            comments?.TryGetValue(name, out rawComment);
            var normalizedComment = string.IsNullOrWhiteSpace(rawComment)
                ? "" : Regex.Replace(rawComment.Trim(), @"\s+", " ");
            return normalizedComment.Length == 0 ? name : $"{name}（{normalizedComment}）";
        });
    }
}
