using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Identity.Client;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using WorkerContagem.Data;
using WorkerContagem.Kafka;
using WorkerContagem.Models;
using WorkerContagem.Tracing;

namespace WorkerContagem;

public class Worker : BackgroundService
{
    private readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;

    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _configuration;
    private readonly ContagemRepository _repository;
    private readonly string _topic;
    private readonly string _groupId;
    private readonly IConsumer<Ignore, string> _consumer;
    private readonly int _intervaloMensagemWorkerAtivo;

    public Worker(ILogger<Worker> logger,
        IConfiguration configuration,
        ContagemRepository repository)
    {
        _logger = logger;
        _configuration = configuration;
        _repository = repository;
        _topic = _configuration["ApacheKafka:Topic"];
        _groupId = _configuration["ApacheKafka:GroupId"];
        _consumer = KafkaExtensions.CreateConsumer(_configuration);
        _intervaloMensagemWorkerAtivo =
            Convert.ToInt32(configuration["IntervaloMensagemWorkerAtivo"]);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Solução que serviu de base para a implementação deste exemplo:
        // https://github.com/open-telemetry/opentelemetry-dotnet/tree/main/examples/MicroserviceExample

        _logger.LogInformation($"Topic = {_topic}");
        _logger.LogInformation($"Group Id = {_groupId}");
        _logger.LogInformation("Aguardando mensagens...");
        _consumer.Subscribe(_topic);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Run(() =>
            {
                var result = _consumer.Consume(stoppingToken);

                // Extrai o PropagationContext de forma a identificar os message headers
                var parentContext = Propagator.Extract(default, result.Message.Headers, this.ExtractTraceContextFromHeaders);
                Baggage.Current = parentContext.Baggage;

                var messageContent = result.Message.Value;
                var partition = result.Partition.Value;

                // Semantic convention - OpenTelemetry messaging specification:
                // https://github.com/open-telemetry/opentelemetry-specification/blob/main/specification/trace/semantic_conventions/messaging.md#span-name
                var activityName = $"{_topic} receive";

                using var activity = OpenTelemetryExtensions.CreateActivitySource()
                    .StartActivity(activityName, ActivityKind.Consumer, parentContext.ActivityContext);
                activity?.SetTag("message", messageContent);
                activity?.SetTag("messaging.system", "kafka");
                activity?.SetTag("messaging.destination_kind", "topic");
                activity?.SetTag("messaging.destination", _topic);
                activity?.SetTag("messaging.operation", "process");
                activity?.SetTag("messaging.kafka.consumer_group", _groupId);
                activity?.SetTag("messaging.kafka.client_id",
                    $"{nameof(WorkerContagem)}-{Environment.MachineName}");
                activity?.SetTag("messaging.kafka.partition", partition);

                _logger.LogInformation(
                    $"[{_topic} | {_groupId} | Nova mensagem] " +
                    messageContent);

                ProcessarResultado(messageContent, partition);
            });
        }
    }

    private void ProcessarResultado(string dados, int partition)
    {
        ResultadoContador? resultado;
        try
        {
            resultado = JsonSerializer.Deserialize<ResultadoContador>(dados,
                new JsonSerializerOptions()
                {
                    PropertyNameCaseInsensitive = true
                });
        }
        catch
        {
            _logger.LogError("Dados inválidos para o Resultado");
            resultado = null;
        }

        if (resultado is not null)
        {
            try
            {
                _repository.Save(resultado, partition);
                _logger.LogInformation("Resultado registrado com sucesso!");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Erro durante a gravacao: {ex.Message}");
            }
        }
    }

    private IEnumerable<string> ExtractTraceContextFromHeaders(Headers headers, string key)
    {
        try
        {
            var header = headers.FirstOrDefault(h => h.Key == key);
            if (header is not null)
                return new[] { Encoding.UTF8.GetString(header.GetValueBytes()) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Falha durante a extração do trace context: {ex.Message}");
        }

        return Enumerable.Empty<string>();
    }
}