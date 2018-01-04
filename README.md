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

1. Compile this project (or get it from the download section) and install as a separate application on your IIS, or
2. Copy _HighlightingProxyHandler.cs_ to your project and add the handler to _Web.config_.

We recommend installing the handler on path _/highlighter/_ as we also reference this path throughout our documentation.

## Configuration

By default, the proxy handler will try to figure out proxy path automatically and redirect requests to PDF Highlighter service running on the same host (on port 8998).

For available configuration options see _Web.config_.
