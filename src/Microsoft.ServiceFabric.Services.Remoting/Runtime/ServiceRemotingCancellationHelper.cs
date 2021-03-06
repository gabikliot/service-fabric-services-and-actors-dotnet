﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------
namespace Microsoft.ServiceFabric.Services.Remoting.Runtime
{
    using System;
    using System.Collections.Concurrent;
    using System.Runtime.ExceptionServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.ServiceFabric.Services.Remoting;

    /// <summary>
    /// Provides cancellation support for remote method dispatching.
    /// </summary>
    internal sealed class ServiceRemotingCancellationHelper
    {
        private ConcurrentDictionary<int, ServiceRemotingCancellationTracker> requestCancellationTracker;
        private const string traceType = "ServiceRemotingCancellationHelper";
        private string traceId;

        public ServiceRemotingCancellationHelper(string traceId)
        {
            this.traceId = traceId;
            this.requestCancellationTracker = new ConcurrentDictionary<int, ServiceRemotingCancellationTracker>();
        }

        public bool IsCancellationRequest(ServiceRemotingMessageHeaders messageHeaders)
        {
            byte[] headerValue;
            if (messageHeaders.InvocationId != null &&
                messageHeaders.TryGetHeaderValue(ServiceRemotingMessageHeaders.CancellationHeaderName, out headerValue))
            {
                return true;
            }

            return false;
        }

        public async Task<byte[]> CancelRequestAsync(
            int interfaceId,
            int methodId,
            string callContext)
        {
            if (callContext != null)
            {
                var cancellationTracker = this.GetCancellationTracker(interfaceId);

                var cancellationTokenResult = await cancellationTracker.TryGetCancellationTokenSource(
                    methodId,
                    callContext);

                if (cancellationTokenResult.CancellationTokenValid)
                {
                    ServiceTrace.Source.WriteInfoWithId(
                        traceType,
                        this.traceId,
                        "Cancelling method call - CallContext : {0}, InterfaceId : {1}, MethodId : {2}",
                        callContext,
                        interfaceId,
                        methodId);

                    // The cancellation token source will be disposed when the method tracked by the CallContext returns.
                    cancellationTokenResult.CancellationTknSource.Cancel();
                }
                else
                {
                    ServiceTrace.Source.WriteInfoWithId(
                        traceType,
                        this.traceId,
                        "Method call - CallContext : {0}, InterfaceId : {1}, MethodId : {2}, is not tracked for cancellation, so it will not be cancelled",
                        callContext,
                        interfaceId,
                        methodId);
                }
            }
            else
            {
                ServiceTrace.Source.WriteWarningWithId(
                    traceType,
                    this.traceId,
                    "Cancel was called for InterfaceId : {0}, MethodId : {1} with a NULL call context",
                    interfaceId,
                    methodId);
            }

            return null;
        }

        public async Task<byte[]> DispatchRequest(
            int interfaceId,
            int methodId,
            string callContext,
            Func<CancellationToken, Task<byte[]>> dispatchFunc)
        {
            var cancellationToken = CancellationToken.None;
            if (callContext != null)
            {
                var cancellationTracker = this.GetCancellationTracker(interfaceId);
                //
                // A cancellation token is created only when the remoting client specifies a callcontext to track
                // the call.
                //
                var cancellationTokenSource = await cancellationTracker.GetOrAddCancellationTokenSource(
                    methodId,
                    callContext);

                cancellationToken = cancellationTokenSource.Token;
            }

            ExceptionDispatchInfo exceptionToThrow = null;
            byte[] result = null;
            try
            {
                result = await dispatchFunc(cancellationToken);
            }
            catch (Exception e)
            {
                if (callContext != null)
                {
                    exceptionToThrow = ExceptionDispatchInfo.Capture(e);
                }
                else
                {
                    throw;
                }
            }

            //
            // Cleanup the cancellation token source.
            //
            if (callContext != null)
            {
                await this.GetCancellationTracker(interfaceId).TryRemoveCancellationTokenSource(methodId, callContext);
            }

            if (exceptionToThrow != null)
            {
                exceptionToThrow.Throw();
            }

            return result;
        }

        private ServiceRemotingCancellationTracker GetCancellationTracker(int interfaceId)
        {
            return this.requestCancellationTracker.GetOrAdd(interfaceId, obj => new ServiceRemotingCancellationTracker());
        }
    }
}
