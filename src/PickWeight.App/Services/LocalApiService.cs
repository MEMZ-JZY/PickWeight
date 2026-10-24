using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PickWeight.Core.Abstractions;
using PickWeight.Core.Models;

namespace PickWeight_App.Services;

// 本地 API 的响应 DTO。JsonPropertyName 固定线上字段名（历史集成为小写开头），
// 配合 LocalApiJsonContext 源生成序列化，避免裁剪发布（PublishTrimmed）下的
// 反射序列化 IL2026 风险。
internal sealed record ApiError([property: JsonPropertyName("error")] string Error);

internal sealed record ApiHealth(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] string? Version);

internal sealed record ApiRecent(
    [property: JsonPropertyName("drawnAt")] DateTimeOffset? DrawnAt,
    [property: JsonPropertyName("names")] IReadOnlyList<string> Names);

internal sealed record ApiNames([property: JsonPropertyName("names")] IReadOnlyList<string> Names);

internal sealed record ApiStatus([property: JsonPropertyName("status")] string Status);

[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(ApiHealth))]
[JsonSerializable(typeof(ApiRecent))]
[JsonSerializable(typeof(ApiNames))]
[JsonSerializable(typeof(ApiStatus))]
internal sealed partial class LocalApiJsonContext : JsonSerializerContext;

/// <summary>Token-protected, loopback-only control surface for local OBS and classroom integrations.</summary>
public sealed class LocalApiService : IDisposable
{
    private const string Prefix = "http://127.0.0.1:17861/";
    private static readonly TimeSpan DrawWaitTimeout = TimeSpan.FromSeconds(30);
    private readonly AppDataService _data;
    private readonly ISettingsService _settings;
    private readonly IDrawExecutionService _execution;
    private readonly QuickDrawWindowService _quickDraw;
    private readonly IDrawExecutionCoordinator _drawCoordinator;
    private readonly AppLogService _log;
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _stop;

    // Keep the original constructor usable for lightweight callers/tests.  The
    // application composition uses the overload below so the shared quick-draw
    // service (and its prepared-draw handler) is injected.
    public LocalApiService(
        AppDataService data,
        ISettingsService settings,
        IDrawExecutionService execution,
        AppLogService log)
        : this(data, settings, execution, new QuickDrawWindowService(settings), log)
    {
    }

    public LocalApiService(
        AppDataService data,
        ISettingsService settings,
        IDrawExecutionService execution,
        QuickDrawWindowService quickDraw,
        AppLogService log)
        : this(data, settings, execution, quickDraw, quickDraw.ExecutionCoordinator, log)
    {
    }

    public LocalApiService(
        AppDataService data,
        ISettingsService settings,
        IDrawExecutionService execution,
        QuickDrawWindowService quickDraw,
        IDrawExecutionCoordinator drawCoordinator,
        AppLogService log)
    {
        _data = data;
        _settings = settings;
        _execution = execution;
        _quickDraw = quickDraw;
        _drawCoordinator = drawCoordinator;
        _log = log;
    }

    /// <summary>
    /// Starts the loopback listener. The local API is an optional integration
    /// surface, so a port conflict (or a missing URL reservation) must degrade
    /// to "feature unavailable" rather than take the whole application down.
    /// </summary>
    public bool Start()
    {
        if (_stop is not null) return true;
        try
        {
            _listener.Prefixes.Add(Prefix);
            _listener.Start();
        }
        catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
        {
            _ = TryLogAsync($"Local API unavailable on {Prefix} ({exception.GetType().Name}). Continuing without it.");
            return false;
        }

        _stop = new CancellationTokenSource();
        _ = ListenAsync(_stop.Token);
        return true;
    }

    public void Dispose()
    {
        _stop?.Cancel();
        _listener.Close();
        _stop?.Dispose();
        _stop = null;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = HandleAsync(context, cancellationToken);
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                await _log.InfoAsync($"Local API listener error: {exception.GetType().Name}.");

                // Once the listener is no longer running, GetContextAsync fails
                // immediately and forever; keeping the loop would spin the CPU
                // and flood the log instead of idling.
                if (!_listener.IsListening)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsAuthorized(context.Request))
            {
                await WriteJsonAsync(context.Response, 401, new ApiError("Unauthorized"), LocalApiJsonContext.Default.ApiError, cancellationToken);
                return;
            }

            switch (context.Request.HttpMethod, context.Request.Url?.AbsolutePath)
            {
                case ("GET", "/v1/health"):
                    await WriteJsonAsync(
                        context.Response,
                        200,
                        new ApiHealth("ok", typeof(App).Assembly.GetName().Version?.ToString(3)),
                        LocalApiJsonContext.Default.ApiHealth,
                        cancellationToken);
                    break;
                case ("GET", "/v1/recent"):
                    var latest = _data.ActiveClassroom.DrawHistory.OrderByDescending(item => item.DrawnAt).FirstOrDefault();
                    await WriteJsonAsync(
                        context.Response,
                        200,
                        new ApiRecent(latest?.DrawnAt, latest?.MemberNames ?? []),
                        LocalApiJsonContext.Default.ApiRecent,
                        cancellationToken);
                    break;
                case ("POST", "/v1/draw"):
                    var names = await DrawOnUiThreadAsync(cancellationToken);
                    await WriteJsonAsync(context.Response, 200, new ApiNames(names), LocalApiJsonContext.Default.ApiNames, cancellationToken);
                    break;
                case ("POST", "/v1/quick-draw"):
                    var quickDrawResult = await StartQuickDrawOnUiThreadAsync(cancellationToken);
                    switch (quickDrawResult.Status)
                    {
                        case QuickDrawRequestStatus.Accepted:
                            await WriteJsonAsync(context.Response, 202, new ApiStatus("accepted"), LocalApiJsonContext.Default.ApiStatus, cancellationToken);
                            break;
                        case QuickDrawRequestStatus.Busy:
                            await WriteJsonAsync(context.Response, 409, new ApiError("QuickDrawBusy"), LocalApiJsonContext.Default.ApiError, cancellationToken);
                            break;
                        case QuickDrawRequestStatus.NoCandidates:
                            await WriteJsonAsync(context.Response, 422, new ApiError("NoEligibleCandidates"), LocalApiJsonContext.Default.ApiError, cancellationToken);
                            break;
                        case QuickDrawRequestStatus.Unavailable:
                            await WriteJsonAsync(context.Response, 503, new ApiError("QuickDrawUnavailable"), LocalApiJsonContext.Default.ApiError, cancellationToken);
                            break;
                        case QuickDrawRequestStatus.Canceled:
                            await WriteJsonAsync(context.Response, 499, new ApiError("RequestCanceled"), LocalApiJsonContext.Default.ApiError, cancellationToken);
                            break;
                        default:
                            await WriteJsonAsync(context.Response, 500, new ApiError("RequestFailed"), LocalApiJsonContext.Default.ApiError, cancellationToken);
                            break;
                    }

                    break;
                default:
                    await WriteJsonAsync(context.Response, 404, new ApiError("NotFound"), LocalApiJsonContext.Default.ApiError, cancellationToken);
                    break;
            }
        }
        catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await WriteJsonAsync(context.Response, 409, new ApiError("DrawBusy"), LocalApiJsonContext.Default.ApiError, CancellationToken.None);
            }
            catch
            {
                TryCloseResponse(context.Response);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryCloseResponse(context.Response);
        }
        catch (Exception exception)
        {
            await TryLogAsync($"Local API request failed: {exception.GetType().Name}.");
            if (cancellationToken.IsCancellationRequested)
            {
                TryCloseResponse(context.Response);
                return;
            }

            try
            {
                await WriteJsonAsync(context.Response, 500, new ApiError("RequestFailed"), LocalApiJsonContext.Default.ApiError, CancellationToken.None);
            }
            catch
            {
                TryCloseResponse(context.Response);
            }
        }
    }

    private bool IsAuthorized(HttpListenerRequest request) =>
        string.Equals(request.Headers["X-PickWeight-Token"], _settings.Current.LocalApiToken, StringComparison.Ordinal);

    private Task<IReadOnlyList<string>> DrawOnUiThreadAsync(CancellationToken cancellationToken) =>
        DrawOnUiThreadAsync(
            cancellationToken,
            callback => App.DispatcherQueue is not null && App.DispatcherQueue.TryEnqueue(async () => await callback()),
            callbackStartedHook: null);

    private Task<IReadOnlyList<string>> DrawOnUiThreadAsync(
        CancellationToken cancellationToken,
        Func<Func<Task>, bool> tryEnqueue,
        Action? callbackStartedHook)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
            return completion.Task;
        }

        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationCancellation.CancelAfter(DrawWaitTimeout);
        var operationToken = operationCancellation.Token;
        var callbackStarted = 0;
        var cancellationRegistration = operationToken.Register(() =>
        {
            if (Volatile.Read(ref callbackStarted) != 0)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            else
            {
                completion.TrySetException(new TimeoutException("等待其他抽取完成超时。"));
            }
        });
        _ = completion.Task.ContinueWith(
            _ =>
            {
                cancellationRegistration.Dispose();
                operationCancellation.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

        var enqueued = false;
        try
        {
            enqueued = tryEnqueue(async () =>
            {
                if (Interlocked.Exchange(ref callbackStarted, 1) != 0)
                {
                    return;
                }

                callbackStartedHook?.Invoke();
                if (operationToken.IsCancellationRequested)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        completion.TrySetCanceled(cancellationToken);
                    }
                    else
                    {
                        completion.TrySetException(new TimeoutException("等待其他抽取完成超时。"));
                    }

                    return;
                }

                try
                {
                    completion.TrySetResult(await ExecuteDrawOperationAsync(operationToken));
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetException(new TimeoutException("等待其他抽取完成超时。"));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }

        if (!enqueued && !completion.Task.IsCompleted)
        {
            completion.TrySetException(new InvalidOperationException("应用 UI 线程不可用。"));
        }
        return completion.Task;
    }

    private async Task<IReadOnlyList<string>> ExecuteDrawOperationAsync(CancellationToken operationToken)
    {
        DrawExecutionLease? drawLease = null;
        PreparedDraw? prepared = null;
        var committed = false;
        IReadOnlyList<string> committedNames = [];
        try
        {
            drawLease = await _drawCoordinator.AcquireAsync(operationToken);
            operationToken.ThrowIfCancellationRequested();

            var settings = _settings.Current;
            prepared = await _execution.PrepareAsync(new DrawExecutionRequest(
                Math.Max(1, settings.DrawTargetCount),
                new DrawFilterOptions(settings.DrawIncludeGroupName, settings.DrawExcludeGroupName,
                    Math.Max(0, settings.DrawExcludeRecentDrawCount), settings.DrawOnlyNeverDrawn),
                settings.DrawMode), operationToken);
            var outcome = await _execution.CommitAsync(
                prepared,
                prepared.SelectedMembers,
                confirmSelection: true,
                cancellationToken: operationToken);
            committed = true;
            committedNames = outcome.CommittedNames;
            operationToken.ThrowIfCancellationRequested();
            return committedNames;
                }
        catch (OperationCanceledException)
        {
            if (!committed && (prepared is null || prepared.CommittedNames.Count == 0))
            {
                await RollbackPreparedAsync(prepared);
                throw;
            }

            return prepared?.CommittedNames?.ToList() ?? committedNames;
        }
        catch
        {
            if (!committed && (prepared is null || prepared.CommittedNames.Count == 0))
            {
                await RollbackPreparedAsync(prepared);
            }

            throw;
        }
        finally
        {
            drawLease?.Dispose();
        }
    }

    private async Task RollbackPreparedAsync(PreparedDraw? prepared)
    {
        if (prepared is null)
        {
            return;
        }

        try
        {
            await _execution.DiscardAsync(prepared, CancellationToken.None);
        }
        catch (Exception exception)
        {
            await TryLogAsync($"Local API draw rollback failed ({exception.GetType().Name}).");
        }
    }

    private Task<QuickDrawRequestResult> StartQuickDrawOnUiThreadAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<QuickDrawRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
            return completion.Task;
        }

        var cancellationRegistration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        _ = completion.Task.ContinueWith(
            _ => cancellationRegistration.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

        var enqueued = false;
        try
        {
            if (App.DispatcherQueue is null)
            {
                completion.TrySetResult(new QuickDrawRequestResult(QuickDrawRequestStatus.Unavailable));
            }
            else
            {
                enqueued = App.DispatcherQueue.TryEnqueue(async () =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        completion.TrySetResult(await _quickDraw.TryStartQuickDrawAsync(cancellationToken));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        completion.TrySetResult(new QuickDrawRequestResult(QuickDrawRequestStatus.Canceled));
                    }
                    catch (Exception exception) { completion.TrySetException(exception); }
                });
            }
        }
        catch (Exception)
        {
            completion.TrySetResult(new QuickDrawRequestResult(QuickDrawRequestStatus.Unavailable));
        }

        if (!enqueued && !completion.Task.IsCompleted)
        {
            completion.TrySetResult(new QuickDrawRequestResult(QuickDrawRequestStatus.Unavailable));
        }

        return completion.Task;
    }

    private static async Task WriteJsonAsync<T>(
        HttpListenerResponse response,
        int statusCode,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        response.ContentLength64 = bytes.Length;
        try
        {
            await response.OutputStream.WriteAsync(bytes, cancellationToken);
        }
        finally
        {
            TryCloseResponse(response);
        }
    }

    private async Task TryLogAsync(string message)
    {
        try
        {
            await _log.InfoAsync(message, CancellationToken.None);
        }
        catch
        {
            // A diagnostics failure must not trigger a second response write.
        }
    }

    private static void TryCloseResponse(HttpListenerResponse response)
    {
        try
        {
            response.Close();
        }
        catch
        {
            // The peer may already have closed the connection.
        }
    }
}







