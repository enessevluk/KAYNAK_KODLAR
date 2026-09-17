using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace RavenMapPanel;

public sealed class TerrainDraftCanvas : FrameworkElement
{
    private TerrainDraft draft = new();
    private readonly Stack<List<TerrainStroke>> redo = new();
    private string activeStrokeId = "";
    private Point? lastPoint;
    private Point? pointer;

    public event EventHandler? DraftChanged;

    public TerrainBrushKind ActiveBrush
    {
        get => draft.ActiveBrush;
        set { draft.ActiveBrush = value; InvalidateVisual(); }
    }

    public double BrushRadius
    {
        get => draft.BrushRadius;
        set { draft.BrushRadius = Math.Clamp(value, 0.005, 0.35); InvalidateVisual(); }
    }

    public int SampleCount => draft.Strokes?.Count ?? 0;

    public TerrainDraftCanvas()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
        ClipToBounds = true;
    }

    public void SetDraft(TerrainDraft? value)
    {
        draft = value ?? new TerrainDraft();
        draft.Strokes ??= [];
        redo.Clear();
        InvalidateVisual();
    }

    public bool Undo()
    {
        if (draft.Strokes.Count == 0) return false;
        var id = draft.Strokes[^1].StrokeId;
        var removed = draft.Strokes.Where(x => x.StrokeId == id).ToList();
        draft.Strokes.RemoveAll(x => x.StrokeId == id);
        redo.Push(removed);
        OnDraftChanged();
        return true;
    }

    public bool Redo()
    {
        if (redo.Count == 0) return false;
        draft.Strokes.AddRange(redo.Pop());
        OnDraftChanged();
        return true;
    }

    public bool ClearDraft()
    {
        if (draft.Strokes.Count == 0) return false;
        draft.Strokes.Clear();
        redo.Clear();
        OnDraftChanged();
        return true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(7, 23, 34)), null, bounds);

        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(42, 112, 145, 164)), 1);
        for (var i = 1; i < 10; i++)
        {
            var x = ActualWidth * i / 10d;
            var y = ActualHeight * i / 10d;
            dc.DrawLine(gridPen, new Point(x, 0), new Point(x, ActualHeight));
            dc.DrawLine(gridPen, new Point(0, y), new Point(ActualWidth, y));
        }

        var islandBrush = new SolidColorBrush(Color.FromRgb(24, 49, 47));
        dc.DrawEllipse(islandBrush, new Pen(new SolidColorBrush(Color.FromRgb(55, 92, 87)), 1.2),
            new Point(ActualWidth / 2, ActualHeight / 2), ActualWidth * 0.38, ActualHeight * 0.38);

        foreach (var stroke in draft.Strokes)
        {
            var center = new Point(stroke.X * ActualWidth, stroke.Y * ActualHeight);
            var radius = Math.Max(2, stroke.Radius * Math.Min(ActualWidth, ActualHeight));
            dc.DrawEllipse(BrushFor(stroke.Kind, stroke.Strength), null, center, radius, radius);
        }

        if (pointer is Point p)
        {
            var radius = Math.Max(2, draft.BrushRadius * Math.Min(ActualWidth, ActualHeight));
            dc.DrawEllipse(null, new Pen(Brushes.White, 1.2), p, radius, radius);
        }

        if (draft.Strokes.Count == 0)
        {
            var text = new FormattedText(
                "Fırçayı seçin ve arazi taslağını burada çizin",
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 14, new SolidColorBrush(Color.FromRgb(165, 185, 194)),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(Math.Max(12, (ActualWidth - text.Width) / 2), Math.Max(12, (ActualHeight - text.Height) / 2)));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        CaptureMouse();
        activeStrokeId = Guid.NewGuid().ToString("N");
        lastPoint = null;
        redo.Clear();
        AddPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        pointer = e.GetPosition(this);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
            AddPoint(pointer.Value);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!IsMouseCaptured) pointer = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) ReleaseMouseCapture();
        activeStrokeId = "";
        lastPoint = null;
        e.Handled = true;
    }

    private void AddPoint(Point point)
    {
        if (ActualWidth <= 1 || ActualHeight <= 1) return;
        var minimumSpacing = Math.Max(2, draft.BrushRadius * Math.Min(ActualWidth, ActualHeight) * 0.22);
        if (lastPoint is Point previous && (previous - point).Length < minimumSpacing) return;

        draft.Strokes.Add(new TerrainStroke
        {
            StrokeId = activeStrokeId,
            Kind = draft.ActiveBrush,
            X = Math.Clamp(point.X / ActualWidth, 0, 1),
            Y = Math.Clamp(point.Y / ActualHeight, 0, 1),
            Radius = draft.BrushRadius,
            Strength = 1
        });
        lastPoint = point;
        OnDraftChanged();
    }

    private void OnDraftChanged()
    {
        InvalidateVisual();
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Brush BrushFor(TerrainBrushKind kind, double strength)
    {
        var alpha = (byte)Math.Clamp(70 + strength * 120, 70, 210);
        var color = kind switch
        {
            TerrainBrushKind.Sea => Color.FromArgb(alpha, 20, 79, 117),
            TerrainBrushKind.Temperate => Color.FromArgb(alpha, 61, 133, 76),
            TerrainBrushKind.Dirt => Color.FromArgb(alpha, 151, 105, 61),
            TerrainBrushKind.Arctic => Color.FromArgb(alpha, 206, 231, 239),
            TerrainBrushKind.Mountain => Color.FromArgb(alpha, 112, 119, 124),
            TerrainBrushKind.Lake => Color.FromArgb(alpha, 31, 130, 172),
            _ => Color.FromArgb(alpha, 200, 200, 200)
        };
        return new SolidColorBrush(color);
    }
}
