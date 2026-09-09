namespace Jerboa.Ui;

/// <summary>Peak level bar with a slow fall-back, so short peaks stay readable.</summary>
public sealed class LevelMeter : Control
{
    private static readonly Color Quiet = Color.FromArgb(29, 158, 117);
    private static readonly Color Loud = Color.FromArgb(186, 117, 23);
    private static readonly Color Clipping = Color.FromArgb(163, 45, 45);

    private float _display;

    public LevelMeter()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Height = 9;
    }

    public float Value
    {
        set
        {
            float level = Math.Clamp(value, 0f, 1f);
            float next = Math.Max(level, _display - 0.05f);
            if (Math.Abs(next - _display) < 0.002f) return;
            _display = next;
            Invalidate();
        }
    }

    public void Reset()
    {
        _display = 0f;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var back = new SolidBrush(Color.FromArgb(238, 237, 233)))
            g.FillRectangle(back, 0, 0, Width, Height);

        int filled = (int)(_display * Width);
        if (filled <= 0) return;

        var colour = _display > 0.92f ? Clipping : _display > 0.72f ? Loud : Quiet;
        using var fill = new SolidBrush(colour);
        g.FillRectangle(fill, 0, 0, filled, Height);
    }
}
