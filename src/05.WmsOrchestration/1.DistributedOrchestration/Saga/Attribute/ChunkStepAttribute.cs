namespace Portfolio.WmsOrchestration.Saga
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class ChunkStepAttribute : Attribute
    {
        public int Index { get; }
        public ChunkStepAttribute(int index)
        {
            Index = index;
        }
    }
}
