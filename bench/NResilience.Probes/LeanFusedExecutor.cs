namespace NResilience.Probes;

/// <summary>
/// A toy fused loop that implements only retry and classification, with no attempt buffer, 
/// breaker, budget, or timeout source. It exists to answer how much of the fused-frame
/// advantage survives when the loop becomes realistic. This is only answerable if both
/// shapes are measured on the same harness in the same run.
///
/// The gap between this and <see cref="FusedExecutor"/> represents the price of the real loop.
/// </summary>
public sealed class LeanFusedExecutor
{
    private readonly int _attempts;

    public LeanFusedExecutor(int attempts = 3) => _attempts = attempts;

    public async ValueTask<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        int attempts = 0;

        while (true)
        {
            try
            {
                return await work(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                attempts++;

                if (ProbeClassifier.Classify(exception).Kind != VerdictKind.Transient || attempts >= _attempts)
                {
                    throw;
                }
            }
        }
    }
}
