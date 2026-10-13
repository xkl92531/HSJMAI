namespace HSJM.AI.AppLayer.PLC.DTOS
{
    public class PLC_WriterFragmentDTO
    {
        public long LogicUnitId { get; set; }

        public long? ProjectId { get; set; }

        public long? BlockId { get; set; }

        public string BlockName { get; set; }

        public string BlockType { get; set; }

        public string StationCode { get; set; }

        public string UnitType { get; set; }

        public int? NetworkId { get; set; }

        public string Title { get; set; }

        public string Comment { get; set; }

        public string ProgrammingLanguage { get; set; }

        public int? ReadVarsCount { get; set; }

        public int? WriteVarsCount { get; set; }

        public int? CallsCount { get; set; }

        public string UidSymbolMapJson { get; set; }

        public string RawContent { get; set; }
    }
}