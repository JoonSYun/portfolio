namespace Portfolio.WmsOrchestration.Saga
{
    public class StepRawInfo
    {
        public int Index { get; set; }
        public Type StepType { get; set; } = default!;

        /// <summary>
        /// Worker가 Payload 로딩을 위해 필요한 DTO 타입 이름
        /// </summary>
        public string PayloadTypeName { get; set; } = string.Empty;
    }
}
