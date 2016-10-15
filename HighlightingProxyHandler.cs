using System;
using System.Web;
using System.Web.Configuration;
using System.Net;
using System.IO;
using System.Web.SessionState;
using System.Collections.Specialized;
using System.Text.RegularExpressions;

namespace PDFHighlighter
{
    public class HighlightingProxyHandler : IHttpHandler, IReadOnlySessionState
    {
        private static log4net.ILog log = log4net.LogManager.GetLogger(typeof(HighlightingProxyHandler));

        /// You will need to configure this handler in the web.config file of your 
        /// web and register it with IIS before being able to use it. For more information
        /// see the following link: http://go.microsoft.com/?linkid=8101007

        string hlService;
        string hlLocalPathPrefix;
        string hlRemotePathPrefix;
        bool hlAddAppPathToRedirect = true;
        bool autoPathAdjust = true;
        string proxyExternalUrl;
        Regex highlighterRedirectionOwnPaths = new Regex("/?doc/|/?viewer/|/?hits/"); // TODO parameter for this

        public HighlightingProxyHandler()
        {
            hlService = WebConfigurationManager.AppSettings["highlightingProxyTo"];

            hlLocalPathPrefix = WebConfigurationManager.AppSettings["highlightingProxyLocalPathPrefix"];
            hlRemotePathPrefix = WebConfigurationManager.AppSettings["highlightingProxyRemotePathPrefix"];
            //if (!string.IsNullOrWhiteSpace(hlLocalPathPrefix) && !string.IsNullOrWhiteSpace(hlRemotePathPrefix))
            if (hlLocalPathPrefix != null || hlRemotePathPrefix != null)
                autoPathAdjust = false;

            string mode = WebConfigurationManager.AppSettings["highlightingProxyMode"];
            if (mode != null)
            {
                autoPathAdjust = "auto".Equals(mode);
            }

            log.Debug("hlService = " + hlService);
            log.Debug("autoPathAdjust = " + autoPathAdjust);

            string addAppPath = WebConfigurationManager.AppSettings["highlightingProxyAddAppPathToRedirect"];
            if (addAppPath != null && ("false".Equals(addAppPath) || "no".Equals(addAppPath)))
            {
                hlAddAppPathToRedirect = false;
            }

            string highlightingProxyInternalPathRegex = WebConfigurationManager.AppSettings["highlightingProxyInternalPathRegex"];
            if (string.IsNullOrEmpty(highlightingProxyInternalPathRegex))
            {
                highlightingProxyInternalPathRegex = "/?doc/|/?viewer/|/?hits/";
            }
            log.Info("For internal path matcher using regex: " + highlightingProxyInternalPathRegex);
            highlighterRedirectionOwnPaths = new Regex(highlightingProxyInternalPathRegex);

            proxyExternalUrl = WebConfigurationManager.AppSettings["highlightingProxyExternalUrl"];
        }

        public bool IsReusable
        {
            // Return false in case your Managed Handler cannot be reused for another request.
            // Usually this would be false in case you have some state information preserved per request.
            get { return true; }
        }

        public void ProcessRequest(HttpContext context)
        {
            HttpResponse response = context.Response;

            HttpWebResponse serverResponse = null;
            WebException error = null;
            Exception otherError = null;

            // SEND REQUEST TO HIGHLIGHTER...
            if (!string.IsNullOrWhiteSpace(hlService))
            {
                string url = hlService;
                string qs = context.Request.QueryString.ToString();

                if (autoPathAdjust)
                {
                    string path = context.Request.AppRelativeCurrentExecutionFilePath;
                    log.Debug("  AppRelativeCurrentExecutionFilePath = " + path);
                    if (path.StartsWith("~/"))
                        path = path.Substring(1);
                    int offset = 0;
                    if (path.StartsWith("/") && url.EndsWith("/"))
                        offset = 1;
                    log.Debug("  offset = " + offset);
                    url += path.Substring(offset);
                }
                else if (!string.IsNullOrWhiteSpace(hlLocalPathPrefix))
                {
                    int ind = context.Request.FilePath.IndexOf(hlLocalPathPrefix);
                    if (ind != -1)
                    {
                        string p = context.Request.FilePath.Substring(ind + hlLocalPathPrefix.Length);
                        url = url + p;
                    }
                }
                /*else
                {
                    url += context.Request.FilePath;
                }*/

                //url += context.Request.PathInfo; // removing as always empty in our use case

                if (!string.IsNullOrEmpty(qs))
                    url += "?" + qs;

                if(log.IsDebugEnabled)
                    log.Debug("Send request to: " + url);

                // Create web request
                HttpWebRequest webRequest = (HttpWebRequest) WebRequest.Create(new Uri(url));

                string serviceUrlHeaderName = "X-Highlighter-Service-URL";
                if (proxyExternalUrl != null)
                {
                    if (!string.IsNullOrEmpty(proxyExternalUrl))
                        webRequest.Headers.Add(serviceUrlHeaderName, proxyExternalUrl);

                    log.Debug("Sending hl service url: (conf) " + proxyExternalUrl);
                }
                else
                {
                    Uri extUrl = context.Request.Url;
                    string extUrlStr = extUrl.Scheme + "://" + extUrl.Host + (extUrl.Port != 80 && extUrl.Port != 443 ? ":" + extUrl.Port : "") + context.Request.ApplicationPath;
                    webRequest.Headers.Add(serviceUrlHeaderName, extUrlStr);

                    log.Debug("Sending hl service url: " + extUrlStr);
                }

                webRequest.Headers.Add("X-Forwarded-For", context.Request.UserHostAddress);

                copyHeadersOfInterestForProxying(context.Request.Headers, webRequest);
                webRequest.Method = context.Request.HttpMethod;
                webRequest.UserAgent = context.Request.UserAgent;

                // don't auto handle redirect as we want user to receive URL that opens first matching page
                webRequest.AllowAutoRedirect = false;

                // Send the request to the server
                try
                {
                    // copy content body (needed for POST requests)
                    int contentLength = context.Request.ContentLength;
                    if (contentLength > 0)
                    {
                        MemoryStream ms = new MemoryStream();
                        context.Request.InputStream.CopyTo(ms);
                        byte[] data = ms.ToArray();
                        using(Stream newStream = webRequest.GetRequestStream()) 
                        {
                            newStream.Write(data, 0, data.Length);
                        }
                    }

                    serverResponse = (HttpWebResponse) webRequest.GetResponse();

                    // check if redirection 
                    if ((int)serverResponse.StatusCode >= 300 && (int)serverResponse.StatusCode < 400)
                    {
                        response.StatusCode = (int)serverResponse.StatusCode;
                        copyHeadersOfInterestForResponse(serverResponse.Headers, response);

                        string redirectUri = serverResponse.Headers["Location"];
                        if (!string.IsNullOrWhiteSpace(redirectUri))
                        {
                            if (log.IsInfoEnabled)
                            {
                                log.Info("Request to: " + url);
                                log.Info("  received redirect to: " + redirectUri);
                            }

                            if (autoPathAdjust)
                            {
                                // if redirection path points to Highlighter, convert path
                                if (redirectUri.StartsWith(hlService))
                                {
                                    string path = redirectUri.Substring(hlService.Length);
                                    
                                    if (highlighterRedirectionOwnPaths.IsMatch(path))
                                    {
                                        // if highlighter redirect path matches the regex, we assume the path belongs to highlighter
                                        redirectUri = context.Request.ApplicationPath + path;
                                    }
                                    else
                                    {
                                        // otherwise assuming it's something else so returning as is
                                        redirectUri = path;
                                    }

                                }
                            }
                            else if (!string.IsNullOrWhiteSpace(hlRemotePathPrefix))
                            {
                                int ind = redirectUri.IndexOf(hlRemotePathPrefix);
                                if (ind != -1)
                                {
                                    redirectUri = (hlAddAppPathToRedirect ? context.Request.ApplicationPath : "") +
                                        hlLocalPathPrefix + redirectUri.Substring(ind + hlRemotePathPrefix.Length);
                                }
                            }

                            if (log.IsInfoEnabled)
                                log.Info("Redirect request to: " + redirectUri);

                            response.RedirectLocation = redirectUri;
                        }

                        response.End();
                        return;
                    }
                }
                catch (WebException webExc)
                {
                    error = webExc;
                    log.Error("Failed request to: " + url);
                    log.Error("Error executing highlighting request: " + webExc.Status.ToString());
                    log.Debug("Error executing highlighting request", webExc);
                }
                catch (Exception exc)
                {
                    otherError = exc;
                    log.Error("Failed request to: " + url);
                    log.Debug("Error executing highlighting request", exc);
                }
            }
            else
            {
                log.Warn("Cannot highlight as no config param provided: highlightingProxyTo");
            }

            // Exit if invalid response
            if (serverResponse == null)
            {
                string altUrl = context.Request.Params["altUrl"];
                if (altUrl == null)
                    altUrl = context.Request.Params["uri"];
                if (altUrl != null)
                {
                    log.Warn("Due to invalid highlighter response redirecting request to: " + altUrl);
                    response.Redirect(altUrl, true);
                }
                else if (error != null || otherError != null)
                {
                    response.StatusCode = 500;
                    if (error != null)
                        response.StatusDescription = error.Status.ToString();
                    //response.Write(error.Response);
                    response.End();
                }
                return;
            }

            // READ AND OUTPUT RESPONSE...

            // Configure reponse
            response.StatusCode = (int)serverResponse.StatusCode;
            response.ContentType = serverResponse.ContentType;
            copyHeadersOfInterestForResponse(serverResponse.Headers, response);
            Stream stream = serverResponse.GetResponseStream();

            stream.CopyTo(response.OutputStream);
            stream.Close();
            //serverResponse.Close();
            response.End();
        }

        protected bool isHeaderOfInterest(string key)
        {
            string kl = key.ToLower();
            return (!kl.Equals("content-disposition") && (
                    kl.StartsWith("content-") || kl.StartsWith("if-") || kl.StartsWith("accept") || kl.StartsWith("etag") ||
                    kl.Contains("range") || kl.StartsWith("expire") || kl.Contains("modified") ||
                    kl.StartsWith("x-") || kl.StartsWith("access-control") || kl.Contains("date") ||
                    kl.Contains("cache") || kl.Contains("pragma") || kl.Contains("vary") || kl.Contains("etag")));
        }

        protected void copyHeadersOfInterestForProxying(NameValueCollection headers, HttpWebRequest request)
        {
            foreach (string key in headers.Keys)
            {
                if (isHeaderOfInterest(key))
                {
                    foreach (string value in headers.GetValues(key))
                    {
                        if(log.IsDebugEnabled)
                            log.Debug("Proxy pass header " + key + ": " + value);

                        // have to check restricted headers first...
                        if ("Accept".Equals(key))
                            request.Accept = value;
                        else if ("Connection".Equals(key))
                            request.Connection = value;
                        else if ("Content-Length".Equals(key))
                            request.ContentLength = Convert.ToInt64(value);
                        else if ("Content-Type".Equals(key))
                            request.ContentType = value;
                        else if ("Date".Equals(key))
                        {
                            DateTime ifDate;
                            DateTime.TryParse(value, out ifDate);
                            if (ifDate != DateTime.MinValue)
                                request.Date = ifDate;
                            else
                                log.Warn("Not able to pass unparsable Date: " + value);
                        }
                        else if ("Expect".Equals(key))
                            request.Expect = value;
                        else if ("If-Modified-Since".Equals(key))
                        {
                            DateTime ifModifiedSinceDate;
                            DateTime.TryParse(value, out ifModifiedSinceDate);
                            if (ifModifiedSinceDate != DateTime.MinValue)
                                request.IfModifiedSince = ifModifiedSinceDate;
                            else
                                log.Warn("Not able to pass unparsable If-Modified-Since: " + value);
                        }
                        else if ("Range".Equals(key))
                            addRanges(value, request);
                        else if ("Transfer-Encoding".Equals(key))
                            request.TransferEncoding = value;
                        else
                            request.Headers.Add(key, value);
                    }
                }
                else if (log.IsDebugEnabled)
                {
                    foreach (string value in headers.GetValues(key))
                    {
                        log.Debug("Proxy ignore header " + key + ": " + value);
                    }
                }
            }
        }

        protected void copyHeadersOfInterestForProxying(NameValueCollection headers, WebHeaderCollection requestHeaders)
        {
            foreach (string key in headers.Keys)
            {
                if (isHeaderOfInterest(key))
                {
                    foreach (string value in headers.GetValues(key))
                    {
                        requestHeaders.Add(key, value);
                        if(log.IsDebugEnabled)
                            log.Debug("Proxy pass header " + key + ": " + value);
                    }
                }
                else if (log.IsDebugEnabled)
                {
                    foreach (string value in headers.GetValues(key))
                    {
                        log.Debug("Proxy ignore header " + key + ": " + value);
                    }
                }
            }
        }

        protected void copyHeadersOfInterestForResponse(WebHeaderCollection headers, HttpResponse response)
        {
            foreach (string key in headers.Keys)
            {
                if (isHeaderOfInterest(key))
                {
                    foreach (string value in headers.GetValues(key))
                    {
                        //response.Headers.Add(key, value); // error "This operation requires IIS integrated pipeline mode."
                        response.AppendHeader(key, value);
                        if (log.IsDebugEnabled)
                            log.Debug("Pass response header " + key + ": " + value);
                    }
                }
                else if(log.IsDebugEnabled)
                {
                    foreach (string value in headers.GetValues(key))
                    {
                        log.Debug("Ignore response header " + key + ": " + value);
                    }
                }
            }
        }

        private void addRanges(string rangeHeader, HttpWebRequest request)
        {
            string[] ranges = rangeHeader.Replace("bytes=", string.Empty).Split(",".ToCharArray());
            const int START = 0, END = 1;

            for (int i = 0; i < ranges.Length; i++)
            {
                if (log.IsDebugEnabled)
                    log.Debug("Parse range: " + ranges[i]);

                // Get the START and END values for the current range
                string[] currentRange = ranges[i].Split("-".ToCharArray());

                if (string.IsNullOrEmpty(currentRange[END])) // No end specified
                {
                    if (log.IsDebugEnabled)
                        log.Debug("  add range: " + currentRange[START]);
                    request.AddRange(long.Parse(currentRange[START]));
                }
                else if (string.IsNullOrEmpty(currentRange[START])) // No start specified
                {
                    if (log.IsDebugEnabled)
                        log.Debug("  add range: " + currentRange[END]);
                    request.AddRange(-long.Parse(currentRange[END]));
                }
                else
                {
                    if (log.IsDebugEnabled)
                        log.Debug("  add range: " + currentRange[START] + "-" + currentRange[END]);
                    request.AddRange(long.Parse(currentRange[START]), long.Parse(currentRange[END]));
                }
            } 
        }
    }
}
