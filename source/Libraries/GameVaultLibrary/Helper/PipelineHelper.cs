using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GameVaultLibrary.Helper
{
    internal class PipelineHelper
    {
        private const string GAMEVAULT_PIPE_NAME = "GameVault";
        /// <summary>
        /// Sends a single message to the main running instance
        /// </summary>
        /// <param name="message">The message to send (which should be an uri form)</param>
        /// <param name="expectsResult">True if we expect a response (such as from a Query)</param>
        /// <returns></returns>
        public static async Task<string> SendPipeMessage(string message, CancellationToken cancellationToken, bool expectsResult = false, int timeout = 500)
        {
            string result = null;
            var client = new NamedPipeClientStream(GAMEVAULT_PIPE_NAME);
            StreamWriter writer = null;
            StreamReader reader = null;

            try
            {
                await client.ConnectAsync(timeout, cancellationToken);

                writer = new StreamWriter(client, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(message);

                if (expectsResult)
                {
                    reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);
                    result = await reader.ReadLineAsync();
                }
            }
            finally
            {
                SafeDispose(writer);
                SafeDispose(reader);
                SafeDispose(client);
            }

            return result;
        }

        /// <summary>
        /// Safely dispose anything without throwing an error if it's already disposed or closed or whatever
        /// </summary>
        /// <param name="disposable">The object to dispose</param>
        private static void SafeDispose(IDisposable disposable)
        {
            if (disposable == null)
                return;

            try
            {
                disposable.Dispose();
            }
            catch (Exception) { }
        }
    }
}
