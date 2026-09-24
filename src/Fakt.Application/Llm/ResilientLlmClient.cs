using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Application.Common;
using Fakt.Core.Llm;
using Fakt.Core.Logging;

namespace Fakt.Application.Llm;

public sealed class RetryPolicy
{
    public int MaxAttempts { get; set; } = 6;
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Экспоненциальная задержка с полным jitter; Retry-After провайдера имеет приоритет.</summary>
    public TimeSpan DelayFor(int attempt, TimeSpan? retryAfter, Random random)
    {
        if (retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero)
        {
            var bounded = retryAfter.Value > MaxDelay ? MaxDelay : retryAfter.Value;
            return bounded + TimeSpan.FromMilliseconds(random.Next(0, 500));
        }

        var exponential = BaseDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, attempt - 1));
        var capped = Math.Min(MaxDelay.TotalMilliseconds, exponential);
        return TimeSpan.FromMilliseconds(random.NextDouble() * capped);
    }
}

/// <summary>
/// Обёртка адаптера для задания: ограничение одновременных запросов, лимиты в минуту, бюджет,
/// повторы временных ошибок (429, 5xx, 529, сеть, тайм-аут) с Retry-After и jitter. Ошибки ключа,
/// прав и неизвестной модели не повторяются. Вызов LLM не бывает «ровно один раз»: запрос мог быть
/// оплачен и при сетевом сбое — поэтому идемпотентность обеспечивается на стороне SQL.
/// </summary>
public sealed class ResilientLlmClient
{
    private readonly ILlmAdapter _adapter;
    private readonly LlmRuntimeConfig _config;
    private readonly RetryPolicy _policy;
    private readonly RateLimiter _limiter;
    private readonly BudgetTracker _budget;
    private readonly SemaphoreSlim _concurrency;
    private readonly IDelay _delay;
    private readonly IAppLogger _logger;
    private readonly Random _random = new();

    public ResilientLlmClient(ILlmAdapter adapter, LlmRuntimeConfig config, RetryPolicy policy, BudgetTracker budget, IAppLogger logger, IDelay delay = null, IClock clock = null)
    {
        _adapter = adapter;
        _config = config;
        _policy = policy ?? new RetryPolicy();
        _budget = budget ?? new BudgetTracker(0, 0);
        _logger = logger ?? NullLogger.Instance;
        _delay = delay ?? RealDelay.Instance;
        _limiter = new RateLimiter(config.Profile.RequestsPerMinute, config.Profile.TokensPerMinute, clock, _delay);
        _concurrency = new SemaphoreSlim(Math.Max(1, config.Profile.MaxConcurrentRequests));
    }

    public LlmRuntimeConfig Config => _config;

    public BudgetTracker Budget => _budget;

    public long? JobId { get; set; }

    public long? FileId { get; set; }

    public async Task<LlmResponse> CompleteAsync(LlmJsonRequest request, int estimatedInputTokens, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _budget.EnsureAvailable();
            await _limiter.AcquireAsync(estimatedInputTokens + Math.Min(request.MaxOutputTokens, 2000), cancellationToken).ConfigureAwait(false);
            await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                _budget.RecordRequest();
                var response = await _adapter.CompleteJsonAsync(_config, request, cancellationToken).ConfigureAwait(false);
                _budget.RecordUsage(response.InputTokens, response.OutputTokens);
                _logger.Info("llm.request", $"Запрос {request.Purpose} выполнен", e =>
                {
                    e.JobId = JobId;
                    e.FileId = FileId;
                    e.Stage = "llm";
                    e.DurationMs = stopwatch.ElapsedMilliseconds;
                    e.ProviderRequestId = response.RequestId;
                    e.Data = new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["provider"] = _config.Profile.ProviderId,
                        ["model"] = _config.Profile.ModelId,
                        ["finish"] = response.RawFinishReason,
                        ["input_tokens"] = response.InputTokens,
                        ["output_tokens"] = response.OutputTokens,
                        ["attempt"] = attempt,
                    };
                });
                return response;
            }
            catch (LlmException ex) when (ex.IsTransient && attempt < _policy.MaxAttempts && !cancellationToken.IsCancellationRequested)
            {
                var wait = _policy.DelayFor(attempt, ex.RetryAfter, _random);
                _logger.Warn("llm.retry", $"{ex.KindText}; повтор через {wait.TotalSeconds:0.0} с (попытка {attempt} из {_policy.MaxAttempts})", e =>
                {
                    e.JobId = JobId;
                    e.FileId = FileId;
                    e.Stage = "llm";
                    e.ErrorCode = ex.Kind.ToString();
                    e.ProviderRequestId = ex.RequestId;
                    e.Category = ErrorCategory.Connection;
                });
                _concurrency.Release();
                try
                {
                    await _delay.DelayAsync(wait, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await _concurrency.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (LlmException ex)
            {
                _logger.Error("llm.error", ex.Message, ex.IsTransient ? ErrorCategory.Connection : ErrorCategory.ModelResponse, null, e =>
                {
                    e.JobId = JobId;
                    e.FileId = FileId;
                    e.Stage = "llm";
                    e.ErrorCode = ex.Kind.ToString();
                    e.ProviderRequestId = ex.RequestId;
                });
                throw;
            }
            finally
            {
                _concurrency.Release();
            }
        }
    }
}
