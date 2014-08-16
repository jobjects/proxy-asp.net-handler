using System;
using System.Web;
using System.Web.Configuration;
using System.Net;
using System.IO;
using System.Web.SessionState;

namespace HollidayWebApp
{
    public class HighlightingProxyHandler : IHttpHandler, IReadOnlySessionState
    {
        private static log4net.ILog log = log4net.LogManager.GetLogger(typeof(HighlightingProxyHandler));

        /// You will need to configure this handler in the web.config file of your 
        /// web and register it with IIS before being able to use it. For more information
        /// see the following link: http://go.microsoft.com/?linkid=8101007

        public bool IsReusable
        {
            // Return false in case your Managed Handler cannot be reused for another request.
            // Usually this would be false in case you have some state information preserved per request.
            get { return true; }
        }

        public void ProcessRequest(HttpContext context)
        {
            HttpResponse response = context.Response;

            string altUrl = context.Request.Params["altUrl"];
            HttpWebResponse serverResponse = null;

            // SEND REQUEST TO HIGHLIGHTER...
            string hlService = WebConfigurationManager.AppSettings["highlightingProxiedTo"];
            if (!string.IsNullOrWhiteSpace(hlService))
            {
                string url = null;
                string qs = context.Request.QueryString.ToString();

                string hlPath = WebConfigurationManager.AppSettings["highlightingProxiedServicePath"];
                if (hlPath != null)
                {
                    int ind = context.Request.FilePath.IndexOf(hlPath);
                    if (ind != -1)
                    {
                        url = hlService + context.Request.FilePath.Substring(ind);
                    }
                }
                url += context.Request.PathInfo;

                if (!string.IsNullOrEmpty(qs))
                    url += "?" + qs;

                log.Info("Send request to hl: " + url);

                // Create web request
                HttpWebRequest webRequest = (HttpWebRequest) WebRequest.Create(new Uri(url));
                webRequest.Method = context.Request.HttpMethod;

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

                        int ind = redirectUri.IndexOf(hlPath);
                        if (ind != -1)
                        {
                            redirectUri = redirectUri.Substring(ind);
                            if (context.Request.ApplicationPath.Length > 0 && !context.Request.ApplicationPath.Equals("/"))
                                redirectUri = context.Request.ApplicationPath + redirectUri;
                        }
                        log.Info("Redirect to: " + redirectUri);

                        response.StatusCode = (int)serverResponse.StatusCode;
                        copyHeadersOfInterest(serverResponse.Headers, response);
                        response.RedirectLocation = redirectUri;
                        response.End();
                        return;
                    }
                }
                catch (WebException webExc)
                {
                    log.Error("Error executing hl request: " + webExc.Status.ToString());
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
                log.Warn("Cannot highlight as no config param provided: highlightingProxiedTo");
            }

            // Exit if invalid response
            if (serverResponse == null)
            {
                if (altUrl != null)
                    response.Redirect(altUrl, true);
                return;
            }

            // READ AND OUTPUT RESPONSE...

            // Configure reponse
            response.StatusCode = (int)serverResponse.StatusCode;
            response.ContentType = serverResponse.ContentType;
            copyHeadersOfInterest(serverResponse.Headers, response);
            Stream stream = serverResponse.GetResponseStream();

            stream.CopyTo(response.OutputStream);
            serverResponse.Close();
            stream.Close();
            response.End();
        }

        protected void copyHeadersOfInterest(WebHeaderCollection headers, HttpResponse response)
        {
            foreach (string key in headers.Keys)
            {
                // FIXME copy highlighter debugging headers
                string kl = key.ToLower();
                if (kl.StartsWith("x-") || kl.StartsWith("access-control") || 
                    kl.Contains("cache") || kl.Contains("pragma") || kl.Contains("expire") || kl.Contains("vary") || kl.Contains("etag"))
                {
                    foreach(string value in headers.GetValues(key))
                    {
                        //response.Headers.Add(key, value); // error "This operation requires IIS integrated pipeline mode."
                        response.AppendHeader(key, value);
                    }
                }
            }
        }
    }
}
