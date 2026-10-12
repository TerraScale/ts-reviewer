using TerraScale.Reviewer.Features.Review;

internal sealed class BillingFixture(Func<CancellationToken, Task<string>>? select = null, Action<string?>? confirm = null) : IWorkspaceBudget
{
    internal static IWorkspaceBudget Opus { get; } = new BillingFixture();
    public Task<string> SelectModelAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return select is null ? Task.FromResult(ReviewProvider.PrimaryModel) : select(ct);
    }
    public void ConfirmWorkspace(string? workspace) => confirm?.Invoke(workspace);
}
