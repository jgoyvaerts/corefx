﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http
{
    internal partial class HttpConnection : IDisposable
    {
        private sealed class ConnectionCloseReadStream : HttpContentReadStream
        {
            public ConnectionCloseReadStream(HttpConnection connection) : base(connection)
            {
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                ValidateBufferArgs(buffer, offset, count);
                return ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_connection == null || destination.Length == 0)
                {
                    // Response body fully consumed or the caller didn't ask for any data
                    return 0;
                }

                ValueTask<int> readTask = _connection.ReadAsync(destination);
                int bytesRead;
                if (readTask.IsCompletedSuccessfully)
                {
                    bytesRead = readTask.Result;
                }
                else
                {
                    CancellationTokenRegistration ctr = _connection.RegisterCancellation(cancellationToken);
                    try
                    {
                        bytesRead = await readTask.ConfigureAwait(false);
                    }
                    catch (Exception exc) when (ShouldWrapInOperationCanceledException(exc, cancellationToken))
                    {
                        throw CreateOperationCanceledException(exc, cancellationToken);
                    }
                    finally
                    {
                        ctr.Dispose();
                    }
                }

                if (bytesRead == 0)
                {
                    // If cancellation is requested and tears down the connection, it could cause the read
                    // to return 0, which would otherwise signal the end of the data, but that would lead
                    // the caller to think that it actually received all of the data, rather than it ending
                    // early due to cancellation.  So we prioritize cancellation in this race condition, and
                    // if we read 0 bytes and then find that cancellation has requested, we assume cancellation
                    // was the cause and throw.
                    cancellationToken.ThrowIfCancellationRequested();

                    // We cannot reuse this connection, so close it.
                    _connection.Dispose();
                    _connection = null;
                    return 0;
                }

                return bytesRead;
            }

            public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
            {
                if (destination == null)
                {
                    throw new ArgumentNullException(nameof(destination));
                }
                if (bufferSize <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(bufferSize));
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (_connection == null)
                {
                    // Response body fully consumed
                    return;
                }

                Task copyTask = _connection.CopyToAsync(destination);
                if (!copyTask.IsCompletedSuccessfully)
                {
                    CancellationTokenRegistration ctr = _connection.RegisterCancellation(cancellationToken);
                    try
                    {
                        await copyTask.ConfigureAwait(false);
                    }
                    catch (Exception exc) when (ShouldWrapInOperationCanceledException(exc, cancellationToken))
                    {
                        throw CreateOperationCanceledException(exc, cancellationToken);
                    }
                    finally
                    {
                        ctr.Dispose();
                    }
                }

                // If cancellation is requested and tears down the connection, it could cause the copy
                // to end early but think it ended successfully. So we prioritize cancellation in this
                // race condition, and if we find after the copy has completed that cancellation has
                // been requested, we assume the copy completed due to cancellation and throw.
                cancellationToken.ThrowIfCancellationRequested();

                // We cannot reuse this connection, so close it.
                _connection.Dispose();
                _connection = null;
            }
        }
    }
}