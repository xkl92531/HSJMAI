using System;

namespace HSJM.AI.AppLayer.PLC.DTOS
{
    public class PLC_WriterFragmentQueryDTO
    {
        public long? ProjectId { get; set; }

        public long? BlockId { get; set; }

        public string BlockName { get; set; }

        public string StationCode { get; set; }

        public int? NetworkId { get; set; }

        public string Title { get; set; }

        public string UnitType { get; set; }

        public bool EligibleOnly { get; set; } = true;

        public int MinWriteVarsCount { get; set; } = 1;

        public int RawContentMaxLength { get; set; } = 1200;
    }
}