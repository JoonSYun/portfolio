namespace Portfolio.WmsOrchestration.Model
{
    public interface iMasterEntity
    {
        public string docNo { get; set; }
        public string bCode { get; set; }
        public int totalCount { get; set; }     // 전체 row 수
        public int sendCount { get; set; }      // 보낸 row 수
    }
}
