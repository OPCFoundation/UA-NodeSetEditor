using System.Text.Json.Serialization;

namespace NodeSetEditor.Server.Model
{
    public class ApiResponse<T> where T : class
    {
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public int? NextStart { get; set; }
        public T? Payload { get; set; }

        public ApiResponse()
        {
        }

        public static ApiResponse<T> Create(T payload, int? nextStart = null)
        {
            return new ApiResponse<T>()
            { 
                Payload = payload,
                NextStart = nextStart
            };
        }

        public static ApiResponse<T> Create(string code, Exception e)
        {
            return new ApiResponse<T>()
            {
                ErrorCode = code,
                ErrorMessage = e.Message
            };
        }

        public static ApiResponse<T> Create(string code, string message)
        {
            return new ApiResponse<T>()
            {
                ErrorCode = code,
                ErrorMessage = message
            };
        }
    }
}
