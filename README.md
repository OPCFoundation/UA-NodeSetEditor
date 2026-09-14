## UA NodeSetEditor

### Overview
This repository has the OPC UA NodeSetEditor which was derived from the [CESMII ProfileDesigner](https://profiledesigner.cesmii.net).

The OPC UA NodeSetEditor is a web based tool for creating, editing and validating OPC UA Information Models. It supports the import/export of the OPC UA Information Models in the UANodeSet serialization formats defined by the OPC UA specification. 

The OPC Foundation offers an online version [here](https://uanodeseteditor.opcfoundation.org/).

This repository includes Docker files to allow users to get a local version running provided they have a [PostgreSQL DB](https://www.postgresql.org/) running.

### Building

```
cd .\nodeseteditor.client
npm install
cd ..
Open NodeSetEditor.slnx with VS026.
```

### API Documentation

The backend's REST API is described by `NodeSetEditor.Server/OpenApi/v1.json`, an OpenAPI 3.0
document **generated from the controllers at build time and checked into the repository**. Nothing
generates it at runtime, so the spec that ships is exactly what the build produced.

Building `NodeSetEditor.Server` rewrites the file, so a change to a controller shows up as a diff in
`OpenApi/v1.json` alongside the code change — commit the two together. CI can enforce this with a
`git diff --exit-code NodeSetEditor.Server/OpenApi/v1.json` after a build.

Swagger UI is served at `/swagger`, reading that static file from `/openapi/v1.json`. Both routes
require a signed-in user: the documentation describes every request body and the internal validation
worker endpoints, and nothing there is intended for anonymous readers. The Swagger UI assets are
vendored under `OpenApi/swagger-ui/` rather than loaded from a CDN because the site's
Content-Security-Policy is `script-src 'self'`.
