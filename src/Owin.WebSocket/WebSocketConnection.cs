using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Owin;
using Owin.WebSocket.Extensions;

namespace Owin.WebSocket
{
    public abstract class WebSocketConnection
    {
        private readonly TaskQueue mSendQueue;
        private readonly CancellationTokenSource mCancellToken;
        private System.Net.WebSockets.WebSocket mWebSocket;

        /// <summary>
        /// Maximum message size in bytes for the receive buffer
        /// </summary>
        public int MaxMessageSize { get; private set; }

        /// <summary>
        /// Arguments captured from URI using Regex
        /// </summary>
        public Dictionary<string, string> Arguments { get; private set; }

        /// <summary>
        /// Queue of send operations to the client
        /// </summary>
        public TaskQueue QueueSend { get { return mSendQueue;} }

        protected WebSocketConnection(int maxMessageSize = 1024*64)
        {
            mSendQueue = new TaskQueue();
            mCancellToken = new CancellationTokenSource();
            MaxMessageSize = maxMessageSize;
        }
        
        /// <summary>
        /// Closes the websocket connection
        /// </summary>
        /// <returns></returns>
        public Task Close(WebSocketCloseStatus status, string reason)
        {
            return mWebSocket.CloseAsync(status, reason, CancellationToken.None);
        }

        /// <summary>
        /// Sends data to the client with binary message type
        /// </summary>
        /// <param name="buffer">Data to send</param>
        /// <param name="endOfMessage">End of the message?</param>
        /// <returns>Task to send the data</returns>
        public Task SendAsyncBinary(byte[] buffer, bool endOfMessage)
        {
            return SendAsync(new ArraySegment<byte>(buffer), endOfMessage, WebSocketMessageType.Binary);
        }

        /// <summary>
        /// Sends data to the client with the text message type
        /// </summary>
        /// <param name="buffer">Data to send</param>
        /// <param name="endOfMessage">End of the message?</param>
        /// <returns>Task to send the data</returns>
        public Task SendAsyncText(byte[] buffer, bool endOfMessage)
        {
            return SendAsync(new ArraySegment<byte>(buffer), endOfMessage, WebSocketMessageType.Text);
        }

        /// <summary>
        /// Sends data to the client
        /// </summary>
        /// <param name="buffer">Data to send</param>
        /// <param name="endOfMessage">End of the message?</param>
        /// <param name="type">Message type of the data</param>
        /// <returns>Task to send the data</returns>
        public Task SendAsync(ArraySegment<byte> buffer, bool endOfMessage, WebSocketMessageType type)
        {
            var sendContext = new SendContext { Buffer = buffer, EndOfMessage = endOfMessage, Type = type };

            return mSendQueue.Enqueue(
                async s =>
                    {
                        await mWebSocket.SendAsync(s.Buffer, s.Type, s.EndOfMessage, CancellationToken.None);
                    },
                sendContext);
        }

        /// <summary>
        /// Close the websocket connection using the close handshake
        /// </summary>
        /// <param name="status">Reason for closing the websocket connection</param>
        /// <param name="statusDescription">Human readable explanation of why the connection was closed</param>
        public Task CloseConnection(WebSocketCloseStatus status, string statusDescription)
        {
            return mWebSocket.CloseAsync(status, statusDescription, CancellationToken.None);
        }

        /// <summary>
        /// Fires after the websocket has been opened with the client
        /// </summary>
        public virtual void OnOpen()
        {
        }

        /// <summary>
        /// Fires when data is received from the client
        /// </summary>
        /// <param name="message">Data that was received</param>
        /// <param name="type">Message type of the data</param>
        public virtual void OnMessageReceived(ArraySegment<byte> message, WebSocketMessageType type)
        {
        }

        /// <summary>
        /// Fires with the connection with the client has closed
        /// </summary>
        public virtual void OnClose()
        {
        }

        /// <summary>
        /// Receive one entire message from the web socket
        /// </summary>
        protected async Task<Tuple<ArraySegment<byte>, WebSocketMessageType>> ReceiveOneMessage(byte[] buffer)
        {
            var count = 0;
            WebSocketReceiveResult result;
            do
            {
                var segment = new ArraySegment<byte>(buffer, count, buffer.Length - count);
                result = await mWebSocket.ReceiveAsync(segment, mCancellToken.Token);

                count += result.Count;
            }
            while (!result.EndOfMessage);

            return new Tuple<ArraySegment<byte>, WebSocketMessageType>(new ArraySegment<byte>(buffer, 0, count), result.MessageType);
        }

        internal void AcceptSocket(IOwinContext context, IDictionary<string, string> argumentMatches)
        {
            var accept = context.Get<Action<IDictionary<string, object>, Func<IDictionary<string, object>, Task>>>("websocket.Accept");
            if (accept == null)
            {
                throw new InvalidOperationException("Not a web socket request");
            }

            Arguments = new Dictionary<string, string>(argumentMatches);

            accept(null, RunWebSocket);
        }

        private async Task RunWebSocket(IDictionary<string, object> websocketContext)
        {
            // Try to get the websocket context from the environment
            object value;
            if (!websocketContext.TryGetValue(typeof(WebSocketContext).FullName, out value))
            {
                throw new InvalidOperationException("Unable to find web socket context");
            }

            mWebSocket = ((WebSocketContext)value).WebSocket;

            OnOpen();

            var buffer = new byte[MaxMessageSize];
            do
            {
                var received = await ReceiveOneMessage(buffer);
                if (received.Item1.Count > 0)
                    OnMessageReceived(received.Item1, received.Item2);
            }
            while (!mWebSocket.CloseStatus.HasValue);

            await mWebSocket.CloseAsync(mWebSocket.CloseStatus.GetValueOrDefault(WebSocketCloseStatus.Empty),
                mWebSocket.CloseStatusDescription, mCancellToken.Token);

            mCancellToken.Cancel();

            OnClose();
        }
    }

    internal class SendContext
    {
        public ArraySegment<byte> Buffer;
        public bool EndOfMessage;
        public WebSocketMessageType Type;
    }
}