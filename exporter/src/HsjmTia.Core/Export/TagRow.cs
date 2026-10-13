namespace HsjmTia.Core.Export
{
    public class TagRow
    {
        public string Path { get; set; }
        public string TableName { get; set; }
        public string TagName { get; set; }
        public string Address { get; set; }
        public string DataType { get; set; }
        public bool IsSafety { get; set; }
        /// <summary>保持性。仅 M 区支持，取不到时为 "False"。</summary>
        public string Retentive { get; set; }
        public bool HmiVisible { get; set; }
        public bool HmiWritable { get; set; }
        public string Comment { get; set; }
    }
}
