namespace EpubKindleFix;

// A changed target preserves velocity, including a quick hover reversal.
public sealed class SpringValue(double stiffness = 110, double damping = 15.4)
{
    public double Position { get; private set; }
    public double Velocity { get; private set; }
    public double Target { get; set; }
    public bool IsAtRest => Math.Abs(Position - Target) < 0.0007 && Math.Abs(Velocity) < 0.004;

    public void Reset(double value)
    {
        Position = Target = value;
        Velocity = 0;
    }

    // Retune mid-flight; position and velocity carry over.
    public void SetTuning(double newStiffness, double newDamping)
    {
        stiffness = newStiffness;
        damping = newDamping;
    }

    // Adds an instant velocity impulse, e.g. click feedback.
    public void Kick(double velocity) => Velocity += velocity;

    public void Step(double elapsedSeconds)
    {
        var remaining = Math.Clamp(elapsedSeconds, 0, 0.064);
        while (remaining > 0)
        {
            var dt = Math.Min(remaining, 1d / 120);
            Velocity += (stiffness * (Target - Position) - damping * Velocity) * dt;
            Position += Velocity * dt;
            remaining -= dt;
        }
        if (IsAtRest) { Position = Target; Velocity = 0; }
    }
}
