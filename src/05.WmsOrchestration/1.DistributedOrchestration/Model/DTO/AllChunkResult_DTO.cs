namespace Portfolio.WmsOrchestration.Model
{
    public class AllChunkResult_DTO
    {
        public bool IsComplete { get; set; }
        public string AllChunkStatus { get; set; } 
        public int TotalChunkCount { get; set; }
        public int SuccessCount { get; set; }
        public int PartialSuccessCount { get; set; }
        public int FailedCount { get; set; }
    }
}
