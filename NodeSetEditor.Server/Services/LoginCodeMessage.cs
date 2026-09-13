namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// The body of the one-time sign-in code email, shared by every <see cref="IEmailSender"/>
    /// so the wording does not drift between transports.
    /// </summary>
    public static class LoginCodeMessage
    {
        public const string Subject = "NodeSet Editor — Sign-in Code";

        public static string TextBody(string code) =>
            $"Your NodeSet Editor sign-in code is: {code}\r\n\r\n" +
            "This code expires in 10 minutes.\r\n\r\n" +
            "If you did not request this, you can safely ignore this email.";

        public static string HtmlBody(string code) =>
            "<p>Your NodeSet Editor sign-in code is:</p>" +
            $"<h2 style='letter-spacing:4px; font-family:monospace; color:#2c5282'>{code}</h2>" +
            "<p>This code expires in 10 minutes.</p>" +
            "<p>If you did not request this, you can safely ignore this email.</p>";
    }
}
