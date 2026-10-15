using Snapaste.Core;
using Snapaste.UI;
namespace Snapaste.Editor;

public sealed class EditorSurface : FrameworkElement
{
    private readonly EditDocument document;
    public Tool CurrentTool { get; set; }
    public Color Ink { get; set; } = Color.FromRgb(233, 93, 117);
    public double InkSize { get; set; } = 5;
    public double Zoom { get; set; } = 1;
    public Rect Visible { get; set; } = new(0, 0, 2000, 2000);
    public bool DrawBaseImage { get; set; } = true;
    public Annotation? HiddenAnnotation { get; set; }
    public string TextFont { get; set; } = "Microsoft YaHei UI";
    public double TextSize { get; set; } = 24;
    public bool TextBold { get; set; }
    public bool TextBackground { get; set; }
    private Annotation? active, selected, original;
    private Point start;
    private Rect cropDrag = Rect.Empty;
    private Gesture gesture;
    private int handle;
    private bool checkpointed, styleEditing, styleCheckpointed;
    private enum Gesture { None, Move, Resize, Rotate, Endpoint, Draw, Text, Crop }
    public Annotation? Selection
    {
        get { if (selected != null) selected = document.Annotations.FirstOrDefault(a => a.Id == selected.Id); return selected; }
    }
    public bool HasSelection => Selection != null;
    public Tool EffectiveTool => Selection?.Kind ?? CurrentTool;
    public Color DisplayColor => Selection?.Color ?? Ink;
    public double DisplaySize => Selection is { } a ? a.Kind is Tool.Blur or Tool.Mosaic ? a.EffectSize / 2d : a.Stroke * 2 : InkSize;
    public event Action? Changed;
    public event Action<Annotation, bool>? TextRequested;
    public event Action<MouseButtonEventArgs>? EmptySelectionDown;
    public EditorSurface(EditDocument document)
    {
        this.document = document; Focusable = true;
        MouseLeftButtonDown += Down; MouseMove += Move; MouseLeftButtonUp += Up;
        LostMouseCapture += (_, _) => { if (gesture != Gesture.None) EndPointer(); };
    }
    private Point Pixel(MouseEventArgs e) { var p = e.GetPosition(this); return new(p.X / Zoom + document.Crop.X, p.Y / Zoom + document.Crop.Y); }
    private Point Clamp(Point p) => new(Math.Clamp(p.X, document.Crop.Left, document.Crop.Right), Math.Clamp(p.Y, document.Crop.Top, document.Crop.Bottom));
    private void Notify() { InvalidateVisual(); Changed?.Invoke(); }
    public void SelectAnnotation(Annotation annotation) { selected = document.Annotations.FirstOrDefault(a => a.Id == annotation.Id); Notify(); }
    public void ClearSelection() { selected = active = original = null; gesture = Gesture.None; if (IsMouseCaptured) ReleaseMouseCapture(); Notify(); }
    public void Delete() { if (Selection is not { } a) return; document.Checkpoint(); document.Annotations.Remove(a); ClearSelection(); }
    public bool MoveSelection(Vector delta) { if (Selection is not { } a) return false; document.Checkpoint(); a.Translate(delta); Notify(); return true; }
    public void BeginStyleEdit() { styleEditing = true; styleCheckpointed = false; }
    public void EndStyleEdit() { styleEditing = styleCheckpointed = false; }
    private void ChangeStyle(Action<Annotation> change)
    {
        if (Selection is not { } a) { Notify(); return; }
        if (!styleEditing || !styleCheckpointed) { document.Checkpoint(); styleCheckpointed = true; }
        change(a); Notify();
    }
    public void SetColor(Color color)
    {
        Ink = color; if (Selection is { } a && a.Color != color) ChangeStyle(x => x.Color = color); else Notify();
    }
    public void SetSize(double value)
    {
        InkSize = value;
        if (Selection is { } a && Math.Abs(DisplaySize - value) > .001 && a.Kind is not Tool.Text)
            ChangeStyle(x => { if (x.Kind is Tool.Blur or Tool.Mosaic) x.EffectSize = Math.Clamp((int)Math.Round(value * 2), 2, 64); else x.Stroke = Math.Max(1, value / 2); });
        else Notify();
    }
    public void SetTextStyle(string font, double size, bool bold, bool background)
    {
        TextFont = font; TextSize = size; TextBold = bold; TextBackground = background;
        if (Selection is { Kind: Tool.Text } a && (a.Font != font || a.FontSize != size || a.Bold != bold || a.Background != background))
            ChangeStyle(x => { x.Font = font; x.FontSize = size; x.Bold = bold; x.Background = background; x.MeasureText(); });
        else Notify();
    }
    public void RotateSelection(double degrees) { if (Selection is not { } a) return; document.Checkpoint(); a.Rotation = (a.Rotation + degrees) % 360; Notify(); }
    public void FlipSelection(bool horizontal) { if (Selection is not { } a) return; document.Checkpoint(); if (horizontal) a.FlipX = !a.FlipX; else a.FlipY = !a.FlipY; Notify(); }
    public void ReverseDirection()
    {
        if (Selection is not { Kind: Tool.Arrow or Tool.Line } a) return;
        document.Checkpoint(); a.Points.Reverse(); Notify();
    }
    private static Point[] Corners(Rect r) => [r.TopLeft, new(r.X + r.Width / 2, r.Top), r.TopRight, new(r.Right, r.Y + r.Height / 2), r.BottomRight, new(r.X + r.Width / 2, r.Bottom), r.BottomLeft, new(r.Left, r.Y + r.Height / 2)];
    private Point RotationHandle(Annotation a) => a.ToWorld(new(a.Bounds.X + a.Bounds.Width / 2, a.Bounds.Y - 26 / Zoom));
    // Handles are evaluated in physical image coordinates, independently of the host's DPI.
    private int HitHandle(Point p)
    {
        if (Selection is not { } a) return -1;
        double radius = 8 / Zoom;
        if ((p - RotationHandle(a)).Length <= radius) return 8;
        if (a.Kind is Tool.Arrow or Tool.Line && a.Points.Count >= 2)
        {
            if ((p - a.ToWorld(a.Points[0])).Length <= radius) return 9;
            if ((p - a.ToWorld(a.Points[^1])).Length <= radius) return 10;
        }
        var points = Corners(a.Bounds);
        for (int i = 0; i < points.Length; i++) if ((p - a.ToWorld(points[i])).Length <= radius) return i;
        return -1;
    }
    public bool HitSelection(Point p) => HitHandle(p) >= 0 || Selection?.HitTest(p, 6 / Zoom) == true;
    private void Down(object sender, MouseButtonEventArgs e)
    {
        Focus(); var p = Pixel(e);
        if (CurrentTool == Tool.Select && !HasSelection && !document.Annotations.Any(a => a.HitTest(p, 6 / Zoom)) && EmptySelectionDown != null) { EmptySelectionDown(e); return; }
        if (BeginPointer(p, e.ClickCount)) CaptureMouse(); e.Handled = true;
    }
    internal bool BeginPointer(Point point, int clicks = 1)
    {
        EndStyleEdit(); start = point; cropDrag = Rect.Empty; checkpointed = false; gesture = Gesture.None;
        if (CurrentTool == Tool.Color)
        {
            var p = Clamp(point); var b = document.Raster(new Rect(Math.Clamp((int)p.X, 0, document.Image.Width - 1), Math.Clamp((int)p.Y, 0, document.Image.Height - 1), 1, 1)); var bytes = new byte[4]; b.CopyPixels(bytes, 4, 0);
            SetColor(Color.FromRgb(bytes[2], bytes[1], bytes[0])); Clipboard.SetText($"#{Ink.R:X2}{Ink.G:X2}{Ink.B:X2}"); return false;
        }
        handle = HitHandle(point);
        if (handle >= 0)
        {
            active = Selection; gesture = handle == 8 ? Gesture.Rotate : handle >= 9 ? Gesture.Endpoint : Gesture.Resize;
        }
        else if (CurrentTool != Tool.Crop && document.Annotations.LastOrDefault(a => a.HitTest(point, 6 / Zoom)) is { } hit)
        {
            selected = active = hit;
            if (clicks == 2 && hit.Kind == Tool.Text) { EditText(hit, false); Notify(); return false; }
            gesture = Gesture.Move;
        }
        else
        {
            bool wasSelected = HasSelection; selected = null; active = null;
            if (wasSelected && CurrentTool is Tool.Text or Tool.Select) { Notify(); return false; }
            start = Clamp(point);
            if (CurrentTool == Tool.Select) { Notify(); return false; }
            gesture = CurrentTool == Tool.Crop ? Gesture.Crop : CurrentTool == Tool.Text ? Gesture.Text : Gesture.Draw;
            if (gesture == Gesture.Draw) { document.Checkpoint(); checkpointed = true; active = NewAnnotation(); document.Annotations.Add(active); selected = active; }
        }
        original = active?.Clone(); Notify(); return true;
    }
    private Annotation NewAnnotation() => new() { Kind = CurrentTool, Color = Ink, Stroke = Math.Max(1, InkSize / 2), EffectSize = Math.Clamp((int)Math.Round(InkSize * 2), 2, 64), Bounds = new Rect(start, new Size(1, 1)), Points = [start], Font = TextFont, FontSize = TextSize, Bold = TextBold, Background = TextBackground };
    private void Move(object sender, MouseEventArgs e)
    {
        var p = Pixel(e);
        if (IsMouseCaptured) UpdatePointer(p);
        else { int h = HitHandle(p); Cursor = h == 8 ? Cursors.Hand : h >= 9 ? Cursors.Cross : h is 0 or 4 ? Cursors.SizeNWSE : h is 2 or 6 ? Cursors.SizeNESW : h is 1 or 5 ? Cursors.SizeNS : h is 3 or 7 ? Cursors.SizeWE : document.Annotations.Any(a => a.HitTest(p, 6 / Zoom)) ? Cursors.SizeAll : CurrentTool == Tool.Text ? Cursors.IBeam : CurrentTool == Tool.Select ? Cursors.Arrow : Cursors.Cross; }
    }
    internal void UpdatePointer(Point point)
    {
        if (gesture == Gesture.None || (point - start).Length < .01) return;
        if (gesture is Gesture.Crop or Gesture.Text) cropDrag = new Rect(start, Clamp(point));
        else if (active != null)
        {
            if (!checkpointed) { document.Checkpoint(); checkpointed = true; }
            switch (gesture)
            {
                case Gesture.Move:
                    var delta = point - start; var desired = original!.Bounds.Location + delta; active.Translate(desired - active.Bounds.Location); break;
                case Gesture.Resize: ResizeSelection(active, original!, handle, point); break;
                case Gesture.Rotate:
                    var center = new Point(original!.Bounds.X + original.Bounds.Width / 2, original.Bounds.Y + original.Bounds.Height / 2);
                    var v = start - center; var w = point - center;
                    active.Rotation = original.Rotation + Vector.AngleBetween(v, w);
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) active.Rotation = Math.Round(active.Rotation / 15) * 15;
                    break;
                case Gesture.Endpoint:
                    active.Points = original!.Points.Select(original.ToWorld).ToList(); active.Rotation = 0; active.FlipX = active.FlipY = false;
                    active.Points[handle == 9 ? 0 : active.Points.Count - 1] = point; active.Bounds = new(active.Points[0], active.Points[^1]); break;
                case Gesture.Draw:
                    point = Clamp(point);
                    if (active.Kind is Tool.Pen or Tool.Highlighter) { active.Points.Add(point); var bounds = active.Bounds; bounds.Union(point); active.Bounds = bounds; }
                    else { active.Bounds = new(start, point); active.Points = [start, point]; }
                    break;
            }
        }
        InvalidateVisual();
    }
    internal static void ResizeSelection(Annotation a, Annotation before, int index, Point world)
    {
        var p = before.ToLocal(world); var r = before.Bounds;
        double l = r.Left, t = r.Top, right = r.Right, bottom = r.Bottom;
        if (index is 0 or 6 or 7) l = Math.Min(p.X, right - 2); if (index is 0 or 1 or 2) t = Math.Min(p.Y, bottom - 2);
        if (index is 2 or 3 or 4) right = Math.Max(p.X, l + 2); if (index is 4 or 5 or 6) bottom = Math.Max(p.Y, t + 2);
        var next = new Rect(new Point(l, t), new Point(right, bottom));
        // Restore the gesture's geometry before applying the absolute resize, avoiding drift.
        a.Bounds = before.Bounds; a.Points = [.. before.Points]; a.Resize(next);
        if (a.Kind == Tool.Text)
        {
            if (index is not (3 or 7)) a.FontSize = Math.Clamp(before.FontSize * next.Height / Math.Max(1, r.Height), 8, 300);
            a.TextWidth = next.Width; a.MeasureText();
        }
        int opposite = (index + 4) % 8;
        var fixedPoint = before.ToWorld(Corners(r)[opposite]); var actual = a.ToWorld(Corners(a.Bounds)[opposite]); a.Translate(fixedPoint - actual);
    }
    private void Up(object sender, MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return; UpdatePointer(Pixel(e)); EndPointer(); ReleaseMouseCapture(); e.Handled = true;
    }
    internal void EndPointer()
    {
        var finished = gesture; gesture = Gesture.None;
        if (finished == Gesture.Crop && !cropDrag.IsEmpty && cropDrag.Width >= 2 && cropDrag.Height >= 2)
        { document.Checkpoint(); document.Crop = new(Math.Floor(cropDrag.X), Math.Floor(cropDrag.Y), Math.Floor(cropDrag.Width), Math.Floor(cropDrag.Height)); }
        if (finished == Gesture.Text)
        {
            var a = NewAnnotation(); if (!cropDrag.IsEmpty && cropDrag.Width >= 12) { a.Bounds = cropDrag; a.TextWidth = cropDrag.Width; }
            cropDrag = Rect.Empty; EditText(a, true);
        }
        if (finished == Gesture.Draw && active != null && active.Bounds.Width < 2 && active.Bounds.Height < 2) { document.Annotations.Remove(active); selected = null; }
        cropDrag = Rect.Empty; active = original = null; Notify();
    }
    private void EditText(Annotation annotation, bool add)
    {
        if (TextRequested != null) { TextRequested(annotation, add); return; }
        var dialog = new TextDialog(annotation) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        var draft = annotation.Clone(); draft.Text = dialog.Value; draft.FontSize = dialog.FontSizeValue; draft.Font = dialog.FontName; draft.Bold = dialog.IsBold; draft.Background = dialog.HasBackground;
        ApplyText(annotation, draft, add);
    }
    public void ApplyText(Annotation annotation, Annotation draft, bool add)
    {
        if (add && string.IsNullOrWhiteSpace(draft.Text)) return;
        bool different = add || annotation.Text != draft.Text || annotation.FontSize != draft.FontSize || annotation.Font != draft.Font || annotation.Color != draft.Color || annotation.Bold != draft.Bold || annotation.Background != draft.Background || annotation.TextWidth != draft.TextWidth;
        if (different)
        {
            document.Checkpoint(); annotation.Text = draft.Text; annotation.FontSize = draft.FontSize; annotation.Font = draft.Font; annotation.Color = draft.Color; annotation.Bold = draft.Bold; annotation.Background = draft.Background; annotation.TextWidth = draft.TextWidth;
            annotation.MeasureText();
            if (add) document.Annotations.Add(annotation); else if (string.IsNullOrWhiteSpace(annotation.Text)) document.Annotations.Remove(annotation);
        }
        selected = annotation; Notify();
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        dc.PushTransform(new ScaleTransform(Zoom, Zoom)); dc.PushTransform(new TranslateTransform(-document.Crop.X, -document.Crop.Y));
        if (DrawBaseImage) document.Draw(dc, Visible); else document.DrawAnnotations(dc, Visible, HiddenAnnotation);
        if (Selection is { } a && a != HiddenAnnotation)
        {
            var line = new System.Windows.Media.Pen(Ui.Accent, 1 / Zoom); var dashed = new System.Windows.Media.Pen(Ui.Accent, 1 / Zoom) { DashStyle = DashStyles.Dash };
            var points = Corners(a.Bounds).Select(a.ToWorld).ToArray();
            for (int i = 0; i < 8; i += 2) dc.DrawLine(dashed, points[i], points[(i + 2) % 8]);
            var rotation = RotationHandle(a); dc.DrawLine(line, points[1], rotation); dc.DrawEllipse(Brushes.White, line, rotation, 4 / Zoom, 4 / Zoom);
            foreach (var p in points) dc.DrawRectangle(Brushes.White, line, new(p.X - 3 / Zoom, p.Y - 3 / Zoom, 6 / Zoom, 6 / Zoom));
            if (a.Kind is Tool.Arrow or Tool.Line && a.Points.Count >= 2)
                foreach (var p in new[] { a.ToWorld(a.Points[0]), a.ToWorld(a.Points[^1]) }) dc.DrawEllipse(Ui.Accent, line, p, 4 / Zoom, 4 / Zoom);
        }
        if (!cropDrag.IsEmpty) dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(24, 119, 98, 186)), new System.Windows.Media.Pen(Ui.Accent, 1 / Zoom), cropDrag);
        dc.Pop(); dc.Pop();
    }
}
