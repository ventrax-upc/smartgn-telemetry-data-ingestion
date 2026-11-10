using Grpc.Core;
using Grpc.Core.Interceptors;
using Smartgn.Telemetry.Configuration;

namespace Smartgn.Telemetry.Grpc;

/// <summary>
/// Guards the private gRPC contract with the shared service-to-service
/// bearer token. When no token is configured (local development) the
/// contract is open, matching the other services of the composition.
/// </summary>
public sealed class InternalTokenInterceptor(IntegrationSettings settings) : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        if (!string.IsNullOrEmpty(settings.InternalServiceToken))
        {
            var authorization = context.RequestHeaders.GetValue("authorization");
            if (!string.Equals(authorization, $"Bearer {settings.InternalServiceToken}", StringComparison.Ordinal))
            {
                throw new RpcException(new Status(StatusCode.Unauthenticated, "invalid_internal_token"));
            }
        }

        return await continuation(request, context);
    }
}
