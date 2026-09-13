using System.Text.Json.Serialization;

namespace NodeSetEditor.Server
{
    public class Account
    {
        public int? Id { get; set; }

        public string? Name { get; set; }

        public AuthenticationStatus? LoginStatus { get; set; }
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AuthenticationStatus
    {
        Unknown = 0,
        Unauthenticated = 0,
        Authenticating = 1,
        Authenticated = 2
    }
}
