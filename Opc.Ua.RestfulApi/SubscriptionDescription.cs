using System;
using System.Text.Json.Serialization;

namespace Opc.Ua.RestfulApi
{
    public class SubscriptionDescription
    {
        [JsonPropertyName("subscriptionId")]
        public long SubscriptionId { get; set; }

        [JsonPropertyName("publishingInterval")]
        public double PublishingInterval { get; set; }

        [JsonPropertyName("lifetimeCount")]
        public int? LifetimeCount { get; set; }

        [JsonPropertyName("maxKeepAliveCount")]
        public int? MaxKeepAliveCount { get; set; }

        [JsonPropertyName("maxNotificationsPerPublish")]
        public int? MaxNotificationsPerPublish { get; set; }

        [JsonPropertyName("publishingEnabled")]
        public bool? PublishingEnabled { get; set; }

        [JsonPropertyName("priority")]
        public int? Priority { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTime? CreatedAt { get; set; }
    }
}
