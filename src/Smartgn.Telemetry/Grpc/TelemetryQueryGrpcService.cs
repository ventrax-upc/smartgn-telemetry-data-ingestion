using Grpc.Core;
using Smartgn.Telemetry.Application;
using AppTelemetryQueryService = Smartgn.Telemetry.Application.TelemetryQueryService;

namespace Smartgn.Telemetry.Grpc;

/// <summary>
/// Private read contract (TelemetryQueryService) consumed by Monitoring &amp;
/// Analytics over gRPC. Identifiers and page sizes are bounded before the
/// time-series repository is queried; an empty device list is not an error.
/// </summary>
public sealed class TelemetryQueryGrpcService(
    AppTelemetryQueryService query,
    ILogger<TelemetryQueryGrpcService> logger) : TelemetryQueryService.TelemetryQueryServiceBase
{
    public override async Task<ReadingsBatchResponse> GetReadingsBatch(
        ReadingsBatchRequest request,
        ServerCallContext context)
    {
        var deviceIds = request.DeviceIds;
        if (deviceIds.Count == 0)
        {
            return new ReadingsBatchResponse();
        }

        if (request.FromUnixMs <= 0 || request.ToUnixMs <= 0 || request.FromUnixMs >= request.ToUnixMs)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid_time_range"));
        }

        var readings = await query.GetBatchAsync(
            deviceIds,
            DateTimeOffset.FromUnixTimeMilliseconds(request.FromUnixMs),
            DateTimeOffset.FromUnixTimeMilliseconds(request.ToUnixMs),
            request.Limit,
            context.CancellationToken);

        logger.LogDebug("GetReadingsBatch returned {Count} readings", readings.Count);

        var response = new ReadingsBatchResponse();
        response.Readings.AddRange(readings.Select(ReadingMapper.ToProto));
        return response;
    }

    public override async Task<LatestReadingsResponse> GetLatestReadings(
        LatestReadingsRequest request,
        ServerCallContext context)
    {
        if (request.DeviceIds.Count == 0)
        {
            return new LatestReadingsResponse();
        }

        var readings = await query.GetLatestAsync(
            request.DeviceIds,
            request.Limit,
            context.CancellationToken);

        logger.LogDebug("GetLatestReadings returned {Count} readings", readings.Count);

        var response = new LatestReadingsResponse();
        response.Readings.AddRange(readings.Select(ReadingMapper.ToProto));
        return response;
    }
}
