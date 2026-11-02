namespace Fern.Run.Cli;

/// <summary>
/// What one step of a composite found, and what the command can do about it.
/// Shared by every composite — `install-control-plane` and
/// `inspect-worker-site-readiness` today — because the three states are the
/// composite layer's vocabulary, not one command's private type. The pass that
/// defines them is docs/how-to/subsystem-sequence-review.md.
/// </summary>
public union StepState(StepState.Ready, StepState.Prepares, StepState.Unbuilt)
{
    /// <summary>The machine already satisfies this step.</summary>
    public sealed record Ready(string Detail);

    /// <summary>Not satisfied, and <see cref="Step.Prepare"/> can satisfy it.</summary>
    public sealed record Prepares(string Detail);

    /// <summary>
    /// Not satisfied, and no code in this repository satisfies it. Carries
    /// the command a person can run instead, and the design step it belongs
    /// to, so the gap is actionable rather than merely reported.
    ///
    /// <paramref name="Blocks"/> separates the two kinds of gap, and the
    /// separation is what keeps this command usable. A missing declared
    /// input blocks: no instruction to the operator makes an install
    /// correct without it. A missing guard does not block: the deployment
    /// lock is absent, and "run one install at a time" is a real mitigation
    /// a person can follow, so the run says so loudly and continues. A gap
    /// that blocked everything would make the composite refuse on every
    /// machine until Fern is finished, which helps nobody.
    /// </summary>
    public sealed record Unbuilt(string Detail, string ByHand, string DesignStep, bool Blocks);
}
