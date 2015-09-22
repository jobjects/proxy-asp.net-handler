using System;
using System.Web;
using System.Web.Configuration;
using System.Net;
using System.IO;
using System.Web.SessionState;
using System.Collections.Specialized;

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

        public HighlightingProxyHandler()
        {
            hlService = WebConfigurationManager.AppSettings["highlightingProxyTo"];
            hlLocalPathPrefix = WebConfigurationManager.AppSettings["highlightingProxyLocalPathPrefix"];
            hlRemotePathPrefix = WebConfigurationManager.AppSettings["highlightingProxyRemotePathPrefix"];

            string addAppPath = WebConfigurationManager.AppSettings["highlightingProxyAddAppPathToRedirect"];
            if (addAppPath != null && ("false".Equals(addAppPath) || "no".Equals(addAppPath)))
            {
                hlAddAppPathToRedirect = false;
            }
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

            // SEND REQUEST TO HIGHLIGHTER...
            if (!string.IsNullOrWhiteSpace(hlService))
            {
                string url = hlService;
                string qs = context.Request.QueryString.ToString();

                if (!string.IsNullOrWhiteSpace(hlLocalPathPrefix))
                {
                    int ind = context.Request.FilePath.IndexOf(hlLocalPathPrefix);
                    if (ind != -1)
                    {
                        string p = context.Request.FilePath.Substring(ind + hlLocalPathPrefix.Length);
                        url = url + p;
                    }
                }
                else
                {
                    url += context.Request.FilePath;
                }

                url += context.Request.PathInfo;

                if (!string.IsNullOrEmpty(qs))
                    url += "?" + qs;

                if(log.IsDebugEnabled)
                    log.Debug("Send request to: " + url);

                // Create web request
                HttpWebRequest webRequest = (HttpWebRequest) WebRequest.Create(new Uri(url));

                copyHeadersOfInterestForProxying(context.Request.Headers, webRequest);
                webRequest.Method = context.Request.HttpMethod;
                webRequest.UserAgent = context.Request.UserAgent;

                // don't auto handle redirect as we want user to receive URL that opens first matching page
                webRequest.AllowAutoRedirect = false;

                // Send the request to the server
                try
                {
                    serverResponse = (HttpWebResponse) webRequest.GetResponse();

                    // check if redirection 
                    if ((int)serverResponse.StatusCode >= 300 && (int)serverResponse.StatusCode < 400)
                    {
                        string redirectUri = serverResponse.Headers["Location"];

                        if (log.IsInfoEnabled)
                        {
                            log.Info("Request to: " + url);
                            log.Info("received redirect to: " + redirectUri);
                        }

                        if (!string.IsNullOrWhiteSpace(hlRemotePathPrefix))
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

                        response.StatusCode = (int)serverResponse.StatusCode;
                        copyHeadersOfInterestForResponse(serverResponse.Headers, response);
                        response.RedirectLocation = redirectUri;
                        response.End();
                        return;
                    }
                }
                catch (WebException webExc)
                {
                    log.Error("Failed request to: " + url);
                    log.Error("Error executing highlighting request: " + webExc.Status.ToString());
                    log.Debug("Error executing highlighting request", webExc);
                    /*
                    response.StatusCode = 500;
                    response.StatusDescription = webExc.Status.ToString();
                    response.Write(webExc.Response);
                    response.End();
                    return;
                     * */
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
