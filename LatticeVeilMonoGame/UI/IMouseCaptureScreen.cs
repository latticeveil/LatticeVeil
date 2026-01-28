namespace LatticeVeilMonoGame.UI;

public interface IMouseCaptureScreen
{
    bool WantsMouseCapture { get; }

    void OnMouseCaptureGained();

    void OnMouseCaptureLost();
}
