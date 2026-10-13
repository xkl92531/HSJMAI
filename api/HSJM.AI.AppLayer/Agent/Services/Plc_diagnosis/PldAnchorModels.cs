using System;
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.Agent.Services.Plc_diagnosis
{
    /// <summary>
    /// Structured anchor extracted from Siemens FlgNet / GRAPH condition XML.
    /// It is shared by the extractor and structural constraint augmenter.
    /// </summary>
    //public sealed class PldAnchorBlock
    //{
    //    public PldAnchorType Type { get; set; }

    //    public string Anchor { get; set; } = string.Empty;

    //    public string TypeText { get; set; } = string.Empty;

    //    public List<ContactRef> DirectContacts { get; } = new();

    //    public Dictionary<string, string> OutPins { get; } = new(StringComparer.OrdinalIgnoreCase);

    //    public Dictionary<string, List<ContactRef>> InPinContacts { get; } = new(StringComparer.OrdinalIgnoreCase);
    //}

    //public enum PldAnchorType
    //{
    //    Coil = 0,
    //    FbCall = 1
    //}

    //public sealed record ContactRef(string Polarity, string Name);
}
