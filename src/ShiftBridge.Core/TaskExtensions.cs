namespace ShiftBridge.Core;

public static class TaskExtensions
{
    /// <summary>
    /// Observe l'échec éventuel d'une tâche que plus personne n'attend (opération abandonnée après un délai par <c>WaitAsync</c>) :
    /// son exception ne remonte plus en <see cref="TaskScheduler.UnobservedTaskException"/>.
    /// </summary>
    public static void ObserveFault(this Task task)
    {
        if (task.IsCompleted) { _ = task.Exception; return; }
        task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Attend <paramref name="task"/> au plus <paramref name="timeout"/> (ou jusqu'à <paramref name="ct"/>) ; en cas d'abandon,
    /// l'opération sous-jacente continue seule et son échec éventuel est observé.
    /// </summary>
    public static async Task WaitObservedAsync(this Task task, TimeSpan timeout, CancellationToken ct = default)
    {
        try { await task.WaitAsync(timeout, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { task.ObserveFault(); throw; }
    }

    /// <inheritdoc cref="WaitObservedAsync(Task, TimeSpan, CancellationToken)"/>
    public static async Task<T> WaitObservedAsync<T>(this Task<T> task, TimeSpan timeout, CancellationToken ct = default)
    {
        try { return await task.WaitAsync(timeout, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { task.ObserveFault(); throw; }
    }

    /// <inheritdoc cref="WaitObservedAsync(Task, TimeSpan, CancellationToken)"/>
    public static async Task WaitObservedAsync(this Task task, CancellationToken ct)
    {
        try { await task.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { task.ObserveFault(); throw; }
    }
}
