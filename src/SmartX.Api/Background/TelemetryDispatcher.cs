using SmartX.Api.Services;
using SmartX.Core.Streaming;

namespace SmartX.Api.Background;

/// <summary>
/// The single consumer of the ingestion queue. Each loop waits for a signal, then takes the next packet:
/// the priority heap is always checked before the FIFO queue, so a critical packet that arrives behind
/// thousands of standard packets is processed next.
/// </summary>
public sealed class TelemetryDispatcher : BackgroundService
{
    private readonly IngestPipeline _pipeline;
    private readonly TelemetryIngestionService _ingestion;
    private readonly ILogger<TelemetryDispatcher> _logger;

    public TelemetryDispatcher(IngestPipeline pipeline, TelemetryIngestionService ingestion, ILogger<TelemetryDispatcher> logger)
    {
        _pipeline = pipeline;
        _ingestion = ingestion;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DualLaneQueue<IngestEnvelope> queue = _pipeline.Queue;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await queue.WaitAsync(stoppingToken);

                // Demo switch: while paused, packets keep arriving and pile up in the lanes.
                while (_pipeline.Paused)
                {
                    await Task.Delay(50, stoppingToken);
                }

                if (!queue.TryDequeue(out Dequeued<IngestEnvelope> item))
                {
                    continue;
                }

                try
                {
                    _ingestion.Process(item);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process packet {Sequence} from {Device}", item.Sequence, item.Item.Device.DisplayName);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
