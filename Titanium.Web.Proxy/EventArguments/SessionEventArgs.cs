﻿using System;
using System.IO;
using System.Text;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Decompression;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http.Responses;
using Titanium.Web.Proxy.Extensions;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Network;
using System.Net;
using System.Net.Sockets;

namespace Titanium.Web.Proxy.EventArguments
{
    /// <summary>
    /// Holds info related to a single proxy session (single request/response sequence)
    /// A proxy session is bounded to a single connection from client
    /// A proxy session ends when client terminates connection to proxy
    /// or when server terminates connection from proxy
    /// </summary>
    public class SessionEventArgs : EventArgs, IDisposable
    {
        /// <summary>
        /// Constructor to initialize the proxy
        /// </summary>
        internal SessionEventArgs()
        {
            ProxyClient = new ProxyClient();
            WebSession = new HttpWebClient();
        }

        /// <summary>
        /// Holds a reference to server connection
        /// </summary>
        internal ProxyClient ProxyClient { get; set; }

        /// <summary>
        /// Does this session uses SSL
        /// </summary>
        public bool IsHttps => WebSession.Request.RequestUri.Scheme == Uri.UriSchemeHttps;


        public IPEndPoint ClientEndPoint => (IPEndPoint)TcpClient.Client.RemoteEndPoint;

        /// <summary>
        /// A web session corresponding to a single request/response sequence
        /// within a proxy connection
        /// </summary>
        public HttpWebClient WebSession { get; set; }

        /// <summary>
        /// Reference to client connection
        /// </summary>
        internal TcpClient TcpClient { get; set; }


        /// <summary>
        /// implement any cleanup here
        /// </summary>
        public void Dispose()
        {

        }

        /// <summary>
        /// Read request body content as bytes[] for current session
        /// </summary>
        private async Task ReadRequestBody()
        {
            //GET request don't have a request body to read
            if ((WebSession.Request.Method.ToUpper() != "POST" && WebSession.Request.Method.ToUpper() != "PUT"))
            {
                throw new BodyNotFoundException("Request don't have a body." +
                                                "Please verify that this request is a Http POST/PUT and request content length is greater than zero before accessing the body.");
            }

            //Caching check
            if (WebSession.Request.RequestBody == null)
            {

                //If chunked then its easy just read the whole body with the content length mentioned in the request header

                using (var requestBodyStream = new MemoryStream())
                {
                    //For chunked request we need to read data as they arrive, until we reach a chunk end symbol
                    if (WebSession.Request.IsChunked)
                    {
                        await this.ProxyClient.ClientStreamReader.CopyBytesToStreamChunked(requestBodyStream).ConfigureAwait(false);
                    }
                    else
                    {
                        //If not chunked then its easy just read the whole body with the content length mentioned in the request header
                        if (WebSession.Request.ContentLength > 0)
                        {
                            //If not chunked then its easy just read the amount of bytes mentioned in content length header of response
                            await this.ProxyClient.ClientStreamReader.CopyBytesToStream(requestBodyStream, WebSession.Request.ContentLength).ConfigureAwait(false);

                        }
                        else if(WebSession.Request.HttpVersion.Major == 1 && WebSession.Request.HttpVersion.Minor == 0)
                            await WebSession.ServerConnection.StreamReader.CopyBytesToStream(requestBodyStream, long.MaxValue).ConfigureAwait(false);
                    }
                    WebSession.Request.RequestBody = await GetDecompressedResponseBody(WebSession.Request.ContentEncoding, requestBodyStream.ToArray()).ConfigureAwait(false);
                }

                //Now set the flag to true
                //So that next time we can deliver body from cache
                WebSession.Request.RequestBodyRead = true;
            }
      
        }

        /// <summary>
        /// Read response body as byte[] for current response
        /// </summary>
        private async Task ReadResponseBody()
        {
            //If not already read (not cached yet)
            if (WebSession.Response.ResponseBody == null)
            {
                using (var responseBodyStream = new MemoryStream())
                {
                    //If chuncked the read chunk by chunk until we hit chunk end symbol
                    if (WebSession.Response.IsChunked)
                    {
                        await WebSession.ServerConnection.StreamReader.CopyBytesToStreamChunked(responseBodyStream).ConfigureAwait(false);
                    }
                    else
                    {
                        if (WebSession.Response.ContentLength > 0)
                        {
                            //If not chunked then its easy just read the amount of bytes mentioned in content length header of response
                            await WebSession.ServerConnection.StreamReader.CopyBytesToStream(responseBodyStream, WebSession.Response.ContentLength).ConfigureAwait(false);

                        }
                        else if(WebSession.Response.HttpVersion.Major == 1 && WebSession.Response.HttpVersion.Minor == 0)
                            await WebSession.ServerConnection.StreamReader.CopyBytesToStream(responseBodyStream, long.MaxValue).ConfigureAwait(false);
                    }

                    WebSession.Response.ResponseBody = await GetDecompressedResponseBody(WebSession.Response.ContentEncoding, responseBodyStream.ToArray()).ConfigureAwait(false);

                }
                //set this to true for caching
                WebSession.Response.ResponseBodyRead = true;
            }
        }

        /// <summary>
        /// Gets the request body as bytes
        /// </summary>
        /// <returns></returns>
        public async Task<byte[]> GetRequestBody()
        {
            if (WebSession.Request.RequestLocked)
                throw new Exception("You cannot call this function after request is made to server.");

            await ReadRequestBody().ConfigureAwait(false);
            return WebSession.Request.RequestBody;
        }
        /// <summary>
        /// Gets the request body as string
        /// </summary>
        /// <returns></returns>
        public async Task<string> GetRequestBodyAsString()
        {
            if (WebSession.Request.RequestLocked)
                throw new Exception("You cannot call this function after request is made to server.");


            await ReadRequestBody().ConfigureAwait(false);

            //Use the encoding specified in request to decode the byte[] data to string
            return WebSession.Request.RequestBodyString ?? (WebSession.Request.RequestBodyString = WebSession.Request.Encoding.GetString(WebSession.Request.RequestBody));
        }

        /// <summary>
        /// Sets the request body
        /// </summary>
        /// <param name="body"></param>
        public async Task SetRequestBody(byte[] body)
        {
            if (WebSession.Request.RequestLocked)
                throw new Exception("You cannot call this function after request is made to server.");

            //syphon out the request body from client before setting the new body
            if (!WebSession.Request.RequestBodyRead)
            {
                await ReadRequestBody().ConfigureAwait(false);
            }

            WebSession.Request.RequestBody = body;

            if (WebSession.Request.IsChunked == false)
                WebSession.Request.ContentLength = body.Length;
            else
                WebSession.Request.ContentLength = -1;
        }

        /// <summary>
        /// Sets the body with the specified string
        /// </summary>
        /// <param name="body"></param>
        public async Task SetRequestBodyString(string body)
        {
            if (WebSession.Request.RequestLocked)
                throw new Exception("You cannot call this function after request is made to server.");

            //syphon out the request body from client before setting the new body
            if (!WebSession.Request.RequestBodyRead)
            {
                await ReadRequestBody().ConfigureAwait(false);
            }

            await SetRequestBody(WebSession.Request.Encoding.GetBytes(body));

        }

        /// <summary>
        /// Gets the response body as byte array
        /// </summary>
        /// <returns></returns>
        public async Task<byte[]> GetResponseBody()
        {
            if (!WebSession.Request.RequestLocked)
                throw new Exception("You cannot call this function before request is made to server.");

            await ReadResponseBody().ConfigureAwait(false);
            return WebSession.Response.ResponseBody;
        }

        /// <summary>
        /// Gets the response body as string
        /// </summary>
        /// <returns></returns>
        public async Task<string> GetResponseBodyAsString()
        {
            if (!WebSession.Request.RequestLocked)
                throw new Exception("You cannot call this function before request is made to server.");

            await GetResponseBody().ConfigureAwait(false);

            return WebSession.Response.ResponseBodyString ?? (WebSession.Response.ResponseBodyString = WebSession.Response.Encoding.GetString(WebSession.Response.ResponseBody));
        }

        /// <summary>
        /// Set the response body bytes
        /// </summary>
        /// <param name="body"></param>
        public async Task SetResponseBody(byte[] body)
        {
            if (!WebSession.Request.RequestLocked)
                throw new Exception("You cannot call this function before request is made to server.");

            //syphon out the response body from server before setting the new body
            if (WebSession.Response.ResponseBody == null)
            {
                await GetResponseBody().ConfigureAwait(false);
            }

            WebSession.Response.ResponseBody = body;

            //If there is a content length header update it
            if (WebSession.Response.IsChunked == false)
                WebSession.Response.ContentLength = body.Length;
            else
                WebSession.Response.ContentLength = -1;
        }

        /// <summary>
        /// Replace the response body with the specified string
        /// </summary>
        /// <param name="body"></param>
        public async Task SetResponseBodyString(string body)
        {
            if (!WebSession.Request.RequestLocked)
                throw new Exception("You cannot call this function before request is made to server.");

            //syphon out the response body from server before setting the new body
            if (WebSession.Response.ResponseBody == null)
            {
                await GetResponseBody().ConfigureAwait(false);
            }

            var bodyBytes = WebSession.Response.Encoding.GetBytes(body);

            await SetResponseBody(bodyBytes).ConfigureAwait(false);
        } 

        private async Task<byte[]> GetDecompressedResponseBody(string encodingType, byte[] responseBodyStream)
        {
            var decompressionFactory = new DecompressionFactory();
            var decompressor = decompressionFactory.Create(encodingType);

            return await decompressor.Decompress(responseBodyStream).ConfigureAwait(false);
        }


        /// <summary>
        /// Before request is made to server 
        /// Respond with the specified HTML string to client
        /// and ignore the request 
        /// </summary>
        /// <param name="html"></param>
        public async Task Ok(string html)
        {
            if (WebSession.Request.RequestLocked)
                throw new Exception("You cannot call this function after request is made to server.");

            if (html == null)
                html = string.Empty;

            var result = Encoding.Default.GetBytes(html);

            await Ok(result).ConfigureAwait(false);
        }

        /// <summary>
        /// Before request is made to server 
        /// Respond with the specified byte[] to client
        /// and ignore the request 
        /// </summary>
        /// <param name="body"></param>
        public async Task Ok(byte[] result)
        {
            var response = new OkResponse();

            response.HttpVersion = WebSession.Request.HttpVersion;
            response.ResponseBody = result;

            await Respond(response).ConfigureAwait(false);

            WebSession.Request.CancelRequest = true;
        }

        public async Task Redirect(string url)
        {
            var response = new RedirectResponse();

            response.HttpVersion = WebSession.Request.HttpVersion;
            response.ResponseHeaders.Add(new Models.HttpHeader("Location", url));
            response.ResponseBody = Encoding.ASCII.GetBytes(string.Empty);

            await Respond(response).ConfigureAwait(false);

            WebSession.Request.CancelRequest = true;
        }

        /// a generic responder method 
        public async Task Respond(Response response)
        {
            WebSession.Request.RequestLocked = true;

            response.ResponseLocked = true;
            response.ResponseBodyRead = true;

            WebSession.Response = response;

            await ProxyServer.HandleHttpSessionResponse(this).ConfigureAwait(false);
        }

    }
}