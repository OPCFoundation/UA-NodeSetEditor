using System;

namespace Opc.Ua.RestfulApi
{
    /// <summary>
    /// Custom HTTP header names used by the OPC UA RESTful API.
    /// </summary>
    public static class HttpHeaders
    {
        /// <summary>
        /// ApplicationUri of the target OPC UA server.
        /// Obtained from the /discovery endpoint.
        /// </summary>
        public const string OpcUaServer = "OpcUa-Server";

        /// <summary>
        /// Session identifier for multi-session scenarios.
        /// </summary>
        public const string OpcUaSessionId = "OpcUa-Session-Id";

        /// <summary>
        /// Server timestamp returned in response headers.
        /// </summary>
        public const string OpcUaServerTimestamp = "OpcUa-ServerTimestamp";

        /// <summary>
        /// Top-level OPC UA service result returned in response headers.
        /// </summary>
        public const string OpcUaServiceResult = "OpcUa-ServiceResult";

        /// <summary>
        /// The user's currently selected server, returned in the /discovery response.
        /// </summary>
        public const string OpcUaSelectedServer = "OpcUa-SelectedServer";
    }

    /// <summary>
    /// Helpers for converting between Guid and urn:uuid: URI format.
    /// </summary>
    public static class UrnUtils
    {
        private const string UrnPrefix = "urn:uuid:";

        /// <summary>
        /// Converts a Guid to a urn:uuid: URI string.
        /// </summary>
        public static string ToUrn(Guid id)
        {
            return UrnPrefix + id.ToString("D");
        }

        /// <summary>
        /// Parses a urn:uuid: URI string back to a Guid.
        /// Returns null if the string is not a valid urn:uuid: URI.
        /// </summary>
        public static Guid? ParseUrn(string? urn)
        {
            if (urn == null || !urn.StartsWith(UrnPrefix, StringComparison.OrdinalIgnoreCase))
                return null;

            var guidPart = urn.Substring(UrnPrefix.Length);
            if (Guid.TryParse(guidPart, out var result))
                return result;

            return null;
        }
    }
}
