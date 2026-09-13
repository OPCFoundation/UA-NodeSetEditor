namespace NodeSetEditor.Server.Model
{
    public class UploadResult
    {
        public string? UploadId { get; set; }
        public int ChunksReceived { get; set; }
        public bool IsComplete { get; set; }
        public ModelInfo? Model { get; set; }
    }
}
