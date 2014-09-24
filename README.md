PDF Highlighting Proxy for ASP.NET
==================================

## What is this?

It's a IHttpHandler implementation that allows your ASP.NET application to proxy HTTP requests to internal server running PDF Highlighter (on Tomcat or compatible server).


## Why would I use it?

- Make highlighter available on the same IIS server (and default port) where your application is hosted. There's no need to open port 8080 (or whatever port the Tomcat is running at) on your server or router to allow access to Highlighter from external network.
- Simplifies setup if your IIS application is served via HTTPS and prevents cross-origin issues.
- Allows you to authenticate and/or authorize users accessing your documents via Highlighter.


## How to install?

Two options:

1. Compile this project and install as a separate application on your IIS, or
2. Copy _HighlightingProxyHandler.cs_ to your project and add the handler to _Web.config_.


## Configuration

The proxy handler uses the following configuration parameters:
- _highlightingProxyTo_ - URL to PDF Highlighter application. This is an internal address accessible from your IIS server.
- _highlightingProxyLocalPathPrefix_ - Proxied application path. It's used to extract the rest of path for sending to the highlighting server.
- _highlightingProxyRemotePathPrefix_ - If specified, the remote path prefix will be removed from redirection location received from remote server and the rest of string will be appended to _highlightingProxyLocalPathPrefix_ to form transformed location.

For example (from _Web.config_):

    <appSettings>
      <add key="highlightingProxyTo" value="http://192.168.10.10:8080/highlighter/"/>
      <add key="highlightingProxyLocalPathPrefix" value="highlighter/"/>
      <add key="highlightingProxyRemotePathPrefix" value="highlighter/"/>
    </appSettings>

