// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpSys.Internal;

namespace Microsoft.AspNetCore.Server.HttpSys
{
    internal unsafe class AsyncAcceptContext : IValueTaskSource<RequestContext>, IDisposable
    {
        private static readonly IOCompletionCallback IOCallback = IOWaitCallback;
        private readonly PreAllocatedOverlapped _preallocatedOverlapped;
        private NativeOverlapped* _overlapped;

        // mutable struct; do not make this readonly
        private ManualResetValueTaskSourceCore<RequestContext> _mrvts = new()
        {
            // We want to run continuations on the IO threads
            RunContinuationsAsynchronously = false
        };

        private RequestContext _requestContext;

        internal AsyncAcceptContext(HttpSysListener server)
        {
            Server = server;
            _preallocatedOverlapped = new(IOCallback, state: this, pinData: null);
        }

        internal HttpSysListener Server { get; }

        internal ValueTask<RequestContext> AcceptAsync()
        {
            _mrvts.Reset();

            AllocateNativeRequest();

            uint statusCode = QueueBeginGetContext();
            if (statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS &&
                statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_IO_PENDING)
            {
                // some other bad error, possible(?) return values are:
                // ERROR_INVALID_HANDLE, ERROR_INSUFFICIENT_BUFFER, ERROR_OPERATION_ABORTED
                return ValueTask.FromException<RequestContext>(new HttpSysException((int)statusCode));
            }

            return new ValueTask<RequestContext>(this, _mrvts.Version);
        }

        private static void IOCompleted(AsyncAcceptContext asyncContext, uint errorCode, uint numBytes)
        {
            var complete = false;
            // This is important to stash a ref to as it's a mutable struct
            ref var mrvts = ref asyncContext._mrvts;
            var requestContext = asyncContext._requestContext;
            var requestId = requestContext.RequestId;

            try
            {
                if (errorCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS &&
                    errorCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_MORE_DATA)
                {
                    mrvts.SetException(new HttpSysException((int)errorCode));
                    return;
                }

                HttpSysListener server = asyncContext.Server;
                if (errorCode == UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS)
                {
                    // at this point we have received an unmanaged HTTP_REQUEST and memoryBlob
                    // points to it we need to hook up our authentication handling code here.
                    try
                    {
                        if (server.ValidateRequest(requestContext) && server.ValidateAuth(requestContext))
                        {
                            // It's important that we clear the request context before we set the result
                            // we want to reuse the acceptContext object for future accepts.
                            asyncContext._requestContext = null;

                            // Initialize features here once we're successfully validated the request
                            // TODO: In the future defer this work to the thread pool so we can get off the IO thread
                            // as quickly as possible
                            requestContext.InitializeFeatures();

                            mrvts.SetResult(requestContext);

                            complete = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        server.SendError(requestId, StatusCodes.Status400BadRequest);
                        mrvts.SetException(ex);
                    }
                    finally
                    {
                        if (!complete)
                        {
                            asyncContext.AllocateNativeRequest(size: requestContext.Size);
                        }
                    }
                }
                else
                {
                    //  (uint)backingBuffer.Length - AlignmentPadding
                    asyncContext.AllocateNativeRequest(numBytes, requestId);
                }

                // We need to issue a new request, either because auth failed, or because our buffer was too small the first time.
                if (!complete)
                {
                    uint statusCode = asyncContext.QueueBeginGetContext();

                    if (statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS &&
                        statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_IO_PENDING)
                    {
                        // someother bad error, possible(?) return values are:
                        // ERROR_INVALID_HANDLE, ERROR_INSUFFICIENT_BUFFER, ERROR_OPERATION_ABORTED
                        mrvts.SetException(new HttpSysException((int)statusCode));
                    }
                }
            }
            catch (Exception exception)
            {
                mrvts.SetException(exception);
            }
        }

        private static unsafe void IOWaitCallback(uint errorCode, uint numBytes, NativeOverlapped* nativeOverlapped)
        {
            var acceptContext = (AsyncAcceptContext)ThreadPoolBoundHandle.GetNativeOverlappedState(nativeOverlapped);
            IOCompleted(acceptContext, errorCode, numBytes);
        }

        private uint QueueBeginGetContext()
        {
            uint statusCode = UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS;
            bool retry;
            do
            {
                retry = false;
                uint bytesTransferred = 0;
                statusCode = HttpApi.HttpReceiveHttpRequest(
                    Server.RequestQueue.Handle,
                    _requestContext.RequestId,
                    // Small perf impact by not using HTTP_RECEIVE_REQUEST_FLAG_COPY_BODY
                    // if the request sends header+body in a single TCP packet 
                    (uint)HttpApiTypes.HTTP_FLAGS.NONE,
                    _requestContext.NativeRequest,
                    _requestContext.Size,
                    &bytesTransferred,
                    _overlapped);

                if (statusCode == UnsafeNclNativeMethods.ErrorCodes.ERROR_INVALID_PARAMETER && _requestContext.RequestId != 0)
                {
                    // we might get this if somebody stole our RequestId,
                    // set RequestId to 0 and start all over again with the buffer we just allocated
                    // BUGBUG: how can someone steal our request ID?  seems really bad and in need of fix.
                    _requestContext.RequestId = 0;
                    retry = true;
                }
                else if (statusCode == UnsafeNclNativeMethods.ErrorCodes.ERROR_MORE_DATA)
                {
                    // the buffer was not big enough to fit the headers, we need
                    // to read the RequestId returned, allocate a new buffer of the required size
                    //  (uint)backingBuffer.Length - AlignmentPadding
                    AllocateNativeRequest(bytesTransferred);
                    retry = true;
                }
                else if (statusCode == UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS
                    && HttpSysListener.SkipIOCPCallbackOnSuccess)
                {
                    // IO operation completed synchronously - callback won't be called to signal completion.
                    IOCompleted(this, statusCode, bytesTransferred);
                }
            }
            while (retry);
            return statusCode;
        }

        private void AllocateNativeRequest(uint? size = null, ulong requestId = 0)
        {
            _requestContext?.ReleasePins();
            _requestContext?.Dispose();

            var boundHandle = Server.RequestQueue.BoundHandle;
            if (_overlapped != null)
            {
                boundHandle.FreeNativeOverlapped(_overlapped);
            }

            _requestContext = new RequestContext(Server, size, requestId);
            _overlapped = boundHandle.AllocateNativeOverlapped(_preallocatedOverlapped);
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_requestContext != null)
                {
                    _requestContext.ReleasePins();
                    _requestContext.Dispose();
                    _requestContext = null;

                    var boundHandle = Server.RequestQueue.BoundHandle;

                    if (_overlapped != null)
                    {
                        boundHandle.FreeNativeOverlapped(_overlapped);
                        _overlapped = null;
                    }
                }
            }
        }

        public RequestContext GetResult(short token)
        {
            return _mrvts.GetResult(token);
        }

        public ValueTaskSourceStatus GetStatus(short token)
        {
            return _mrvts.GetStatus(token);
        }

        public void OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            _mrvts.OnCompleted(continuation, state, token, flags);
        }
    }
}
