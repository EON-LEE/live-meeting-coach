using System.Text;
using System.Threading.Channels;

namespace VoiceAssistant.Api;

/// <summary>
/// Generates one session's replies: retrieval, the streamed answer, the turn classifier and the Korean meaning of each line.
/// Everything a reply produces goes out through its <see cref="ReplyOutput"/>, so the session's event loop stays the only
/// writer to the socket and nothing of a speculative reply is seen before its final adopts it.
/// </summary>
internal sealed class ReplyGeneration(IMeetingProvider provider, string objectId, ILogger logger, TimeProvider clock)
{
    private readonly List<Task> translations = [];

    /// <summary>Translations still running, for the session's teardown; none starts once the session lifetime is cancelled.</summary>
    public Task[] PendingTranslations()
    {
        lock (translations) return translations.ToArray();
    }

    public async Task GenerateAsync(ReplyOutput output, ConversationTurn[] history, string route, Task<RetrievalOutcome>? prefetched,
        KoreanScope? scope, SessionOptions settings, MeetingMetrics.ResponseMeasurement measurement, long deadline,
        CancellationToken cancellation, CancellationToken lifetime)
    {
        // Model calls of this reply (answer, classifier) see the time it really has left, after any retrieval wait.
        using var replyDeadline = LiveChatCall.WithinReplyDeadline(deadline, clock);
        var groundingUnavailable = false;
        try
        {
            if (route == "profile")
            {
                cancellation.ThrowIfCancellationRequested();
                measurement.GroundingReady("disabled");
                var introduction = ProfileIntroduction.Compose(settings);
                StartKorean(scope, 0, introduction, new TranslationContext(settings.Topic, settings.Phrases), lifetime);
                await output.WriteAsync(new(SessionEventKind.Delta, introduction, PrimaryDone: true), cancellation);
                await output.WriteAsync(new(SessionEventKind.Complete,
                    new Completion(introduction, [introduction], [], "disabled", "profile", false)), cancellation);
                return;
            }
            // Started before retrieval so the classifier has finished by the time the answer does; it only adds emphasis
            // and never delays or blocks the suggestion. Its failure is observed here so it can never go unhandled.
            var turnSignal = provider.ShouldRespondAsync(history, cancellation);
            _ = turnSignal.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            var grounding = new Grounding("disabled", []);
            if (route == "knowledge")
            {
                var waitStarted = clock.GetTimestamp();
                try
                {
                    grounding = prefetched is not null ? (await prefetched.WaitAsync(cancellation)).RequireGrounding() :
                        await MeetingMetrics.MeasureRetrievalAsync(provider,
                            RetrievalQuery.Build(history[^1].Text, history.Take(history.Length - 1).ToArray()), objectId, cancellation);
                }
                catch (ProviderException failure) when (failure.Code == "grounding_unavailable")
                {
                    output.Log(() => LogRetrievalFailure(logger, failure));
                    // Announced before the reply, as before, so the client knows this reply is not based on its materials.
                    await output.WriteAsync(new(SessionEventKind.GroundingUnavailable), cancellation);
                    groundingUnavailable = true;
                    grounding = new("unavailable", []);
                }
                measurement.GroundingReady(grounding.Status, clock.GetElapsedTime(waitStarted), prefetched is not null);
            }
            else measurement.GroundingReady(grounding.Status);
            var modelResponse = grounding.Status is "disabled" or "grounded" or "no_matches" or "unavailable";
            var translation = scope is null ? TranslationContext.Empty
                : new TranslationContext(settings.Topic, KoreanTranslation.Terms(settings, grounding));
            var text = new StringBuilder();
            string? alternative = null;
            var primaryDoneSent = false;
            measurement.RequestSent();
            await foreach (var update in provider.AnswerWithSuggestionsAsync(history, grounding, settings, route, cancellation).WithCancellation(cancellation))
            {
                cancellation.ThrowIfCancellationRequested();
                var delta = update.Text;
                if (delta.Length > 0) measurement.FirstTokenReceived();
                if (text.Length + delta.Length > 8000) throw new ProviderException("response_limit", "Response exceeded its size limit.");
                text.Append(delta);
                if (update.Alternative is not null) alternative = update.Alternative;
                // The delta that ends the first line carries primaryDone; an end found after that text was sent is an empty delta.
                var primaryDone = update.PrimaryDone && !primaryDoneSent && text.Length > 0;
                primaryDoneSent |= primaryDone;
                if (delta.Length > 0 || primaryDone)
                    await output.WriteAsync(new(SessionEventKind.Delta, delta, ModelResponse: modelResponse, PrimaryDone: primaryDone), cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            if (text.Length == 0) throw new ProviderException("empty_response", "The model returned no text. Please retry.");
            // A single-line reply is known to be complete only when the stream ends.
            if (!primaryDoneSent)
                await output.WriteAsync(new(SessionEventKind.Delta, "", ModelResponse: modelResponse, PrimaryDone: true), cancellation);
            var suggestions = ReplySuggestions.Complete(text.ToString(), alternative);
            // Translation starts only once the stream has ended normally: with asynchronous content filtering a line is not
            // checked before then, and a reply the filter stops (response_filtered) must never reach the translation call.
            for (var index = 0; index < suggestions.Length; index++)
                StartKorean(scope, index, suggestions[index].Trim(), translation, lifetime);
            var classifierStarted = clock.GetTimestamp();
            var (respondNow, signal, classifierFailure) = await TurnSignalAsync(turnSignal);
            measurement.ClassifierWaited(clock.GetElapsedTime(classifierStarted), signal);
            // A classifier cancelled together with its reply (cancelled, superseded or timed out) did not fail.
            if (classifierFailure is not null && !(classifierFailure is OperationCanceledException && cancellation.IsCancellationRequested))
                output.Log(() => logger.LogWarning("Turn classifier failed: {ExceptionType}, HTTP status {Status}; the reply is sent without emphasis.",
                    classifierFailure.GetType().Name, HttpStatusText(classifierFailure)));
            await output.WriteAsync(new(SessionEventKind.Complete,
                new Completion(text.ToString(), suggestions,
                    DistinctSources(grounding.Documents), grounding.Status, route, prefetched is not null, respondNow),
                ModelResponse: modelResponse), cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A deadline after an announced search failure still ends with the fixed completion, never a second error.
            // A user cancel bumps the generation number, so the event loop drops this stale event.
            if (!lifetime.IsCancellationRequested)
                await output.WriteAsync(groundingUnavailable
                    ? new(SessionEventKind.GroundingFallback)
                    : new(SessionEventKind.GenerationError, new ProviderException("response_timeout", "Response timed out or was cancelled.")), lifetime);
        }
        catch (Exception exception)
        {
            if (!lifetime.IsCancellationRequested)
            {
                var error = exception switch
                {
                    ProviderException failure => failure,
                    System.ClientModel.ClientResultException { Status: 429 } =>
                        new ProviderException("model_busy", "Azure model request limit reached. Wait briefly, then retry."),
                    _ => new ProviderException("response_unavailable", "Response service is unavailable. Please retry.")
                };
                // Content-free, like every failure line: a withdrawn reply logs its code and generation, never its text.
                var filtered = error.Code == "response_filtered";
                var afterSearchFailure = groundingUnavailable;
                if (filtered)
                    output.Log(() => logger.LogWarning(
                        "Meeting response withdrawn: code {Code}, generation {Generation}, after search failure {AfterSearchFailure}.",
                        error.Code, output.Generation, afterSearchFailure));
                else
                    output.Log(() => logger.LogWarning(
                        "Meeting response failed with code {Code}: {ExceptionType}, HTTP status {Status}, after search failure {AfterSearchFailure}.",
                        error.Code, exception.GetType().Name, HttpStatusText(exception), afterSearchFailure));
                // The search failure was already announced; when the general reply fails too, only the fixed notice follows.
                // A reply the content filter stopped is withdrawn instead, because that notice would complete it.
                await output.WriteAsync(groundingUnavailable && !filtered
                    ? new(SessionEventKind.GroundingFallback)
                    : new(SessionEventKind.GenerationError, error), lifetime);
            }
        }
    }

    /// <summary>Content-free log line for a search failure: the cause's category, exception type, HTTP status, Search code and retry.</summary>
    internal static void LogRetrievalFailure(ILogger logger, ProviderException failure)
    {
        var cause = failure.Retrieval;
        logger.LogWarning("Meeting retrieval unavailable: {Kind}, transient {Transient}, {ExceptionType}, HTTP status {Status}, search code {SearchCode}, retried {Retried}.",
            cause?.Kind ?? "unknown", cause?.Transient ?? false, cause?.ExceptionType ?? nameof(ProviderException),
            cause?.HttpStatus?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none", cause?.ErrorCode ?? "none",
            cause?.Retried ?? false);
    }

    /// <summary>
    /// One source per document, in evidence order: chunks of the same document share its URL (or, without one, its title),
    /// so they appear once. The model still receives every accepted chunk as evidence.
    /// </summary>
    internal static Source[] DistinctSources(IEnumerable<Evidence> documents)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return documents.Select(document => document.Source)
            .Where(source => seen.Add(string.IsNullOrWhiteSpace(source.Url)
                ? "title:" + source.Title.Trim().ToUpperInvariant() : "url:" + source.Url))
            .ToArray();
    }

    /// <summary>The HTTP status an Azure SDK exception carries, or "none" when no HTTP answer was received.</summary>
    internal static string HttpStatusText(Exception exception) => exception switch
    {
        System.ClientModel.ClientResultException { Status: > 0 } client => client.Status.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Azure.RequestFailedException { Status: > 0 } failed => failed.Status.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => "none"
    };

    /// <summary>
    /// Translates one reply line for a client that opted in. It runs beside the answer, publishes through the event loop and
    /// never blocks the reply; a line that cannot be translated is reported as failed so the client can fall back.
    /// </summary>
    private void StartKorean(KoreanScope? scope, int index, string english, TranslationContext context, CancellationToken lifetime)
    {
        if (scope is null || english.Length == 0 || lifetime.IsCancellationRequested || !scope.Begin(index, english)) return;
        lock (translations)
        {
            // Checked under the lock the cleanup also takes, so nothing starts after the final snapshot.
            if (lifetime.IsCancellationRequested) return;
            translations.RemoveAll(task => task.IsCompleted);
            translations.Add(Task.Run(() => TranslateAsync(scope, index, english, context, lifetime), CancellationToken.None));
        }
    }

    private async Task TranslateAsync(KoreanScope scope, int index, string english, TranslationContext context, CancellationToken lifetime)
    {
        string? korean = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            korean = await provider.TranslateReplyAsync(english, context, timeout.Token);
        }
        catch (Exception exception) when (!lifetime.IsCancellationRequested)
        {
            scope.Output.Log(() => logger.LogWarning("Reply translation failed: {Failure}.",
                exception is System.ClientModel.ClientResultException client ? $"HTTP {client.Status}" : exception.GetType().Name));
        }
        catch (Exception)
        {
            return;
        }
        if (scope.Abandoned || !scope.IsCurrent(index, english) || lifetime.IsCancellationRequested) return;
        try { await scope.Output.WriteAsync(new(SessionEventKind.Korean, new KoreanLine(scope, index, korean)), lifetime); }
        catch (Exception exception) when (exception is OperationCanceledException or ChannelClosedException) { }
    }

    /// <summary>
    /// The turn signal is optional: a slow or failed classifier yields "no emphasis", never an error. A failure is returned
    /// so it can be logged, for example a request the deployment rejects.
    /// </summary>
    private static async Task<(bool RespondNow, string Outcome, Exception? Failure)> TurnSignalAsync(Task<bool> signal)
    {
        try
        {
            var respond = await signal.WaitAsync(TimeSpan.FromMilliseconds(800));
            return (respond, respond ? "respond" : "listen", null);
        }
        catch (TimeoutException) { return (false, "timeout", null); }
        catch (Exception exception) { return (false, "failed", exception); }
    }
}
