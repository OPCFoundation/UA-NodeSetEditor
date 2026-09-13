// Kept out of index.html because the site's Content-Security-Policy is script-src 'self',
// which forbids inline script blocks.
window.ui = SwaggerUIBundle({
    url: "/openapi/v1.json",
    dom_id: "#swagger-ui",
    deepLinking: true,
    displayRequestDuration: true,
    docExpansion: "none",
    filter: true,
    tryItOutEnabled: true,
    // The page is reachable only when signed in, so the browser already holds the session
    // cookie; send it on "Try it out" calls instead of making the user paste a token.
    // Azure AD users still need to Authorize with a bearer token.
    requestInterceptor: function (request) {
        request.credentials = "same-origin";
        return request;
    },
    presets: [SwaggerUIBundle.presets.apis],
});
