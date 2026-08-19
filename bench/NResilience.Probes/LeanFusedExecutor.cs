namespace NResilience.Probes;

/// <summary>
/// The <i>toy</i> fused loop: retry and classification only, with no attempt buffer, no
/// breaker, no budget and no timeout source. This is the shape the probes behind
/// plans/nresilience-design-v3.md actually measured, and it exists here for exactly one
/// reason — open question 1 asks how much of the fused-frame advantage survives when the loop
/// becomes realistic, and that is only answerable if both shapes are measured on the same
/// harness in the same run.
///
/// The gap between this and <see cref="FusedExecutor"/> is the price of the real loop.
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
