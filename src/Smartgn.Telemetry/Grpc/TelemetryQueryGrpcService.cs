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
        var usesSupplyPoints = request.SupplyPointIds.Count > 0;
        var identifiers = usesSupplyPoints ? request.SupplyPointIds : request.DeviceIds;
        if (usesSupplyPoints && !Guid.TryParse(request.AccountId, out _))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_id_required"));
        }
        if (identifiers.Count == 0)
        {
            return new ReadingsBatchResponse();
        }

        if (request.FromUnixMs <= 0 || request.ToUnixMs <= 0 || request.FromUnixMs >= request.ToUnixMs)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid_time_range"));
        }

        var from = DateTimeOffset.FromUnixTimeMilliseconds(request.FromUnixMs);
        var to = DateTimeOffset.FromUnixTimeMilliseconds(request.ToUnixMs);
        var readings = usesSupplyPoints
            ? await query.GetBatchBySupplyPointsAsync(
                identifiers, request.AccountId, from, to, request.Limit, context.CancellationToken)
            : await query.GetBatchAsync(identifiers, from, to, request.Limit, context.CancellationToken);

        logger.LogDebug("GetReadingsBatch returned {Count} readings", readings.Count);

        var response = new ReadingsBatchResponse();
        response.Readings.AddRange(readings.Select(ReadingMapper.ToProto));
        return response;
    }

    public override async Task<LatestReadingsResponse> GetLatestReadings(
        LatestReadingsRequest request,
        ServerCallContext context)
    {
        var usesSupplyPoints = request.SupplyPointIds.Count > 0;
        var identifiers = usesSupplyPoints ? request.SupplyPointIds : request.DeviceIds;
        if (usesSupplyPoints && !Guid.TryParse(request.AccountId, out _))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "account_id_required"));
        }
        if (identifiers.Count == 0)
        {
            return new LatestReadingsResponse();
        }

        var readings = usesSupplyPoints
            ? await query.GetLatestBySupplyPointsAsync(
                identifiers, request.AccountId, request.Limit, context.CancellationToken)
            : await query.GetLatestAsync(identifiers, request.Limit, context.CancellationToken);

        logger.LogDebug("GetLatestReadings returned {Count} readings", readings.Count);

        var response = new LatestReadingsResponse();
        response.Readings.AddRange(readings.Select(ReadingMapper.ToProto));
        return response;
    }
}
