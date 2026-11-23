using System.Globalization;
using System.Text;
using System.Windows.Automation;
using Snapaste.Core;
using Snapaste.Editor;
using Snapaste.UI;

namespace Snapaste.Ocr;

// Shared inline result surface. Recognition never modifies the screenshot or
// annotations; the original engine output remains available for comparison.
public sealed class OcrPanel : Border
{
    private readonly TextBox result = new(), original = new();
    private readonly ComboBox punctuation = new() { MinWidth = 126 };
    private readonly TextBlock status = Ui.Label("", 12), review = Ui.Label("", 11, true);
    private readonly ProgressBar progress = new() { Height = 3, Margin = new Thickness(0, 9, 0, 9) };
    private readonly Button copyOnly, copy, save, retry, cancel, undo, redo;
    private readonly Expander originalSection;
    private CancellationTokenSource? operation;
    private EditDocument? snapshot;
    private string rawText = "";
    private bool dismissed, updating;
    public event Action? CloseRequested;
    public event Action? CopyCompleted;
    public event Action<Vector>? DragRequested;
    public bool IsBusy => operation != null;

    public OcrPanel(bool movable = true)
    {
        CornerRadius = new CornerRadius(12); BorderThickness = new Thickness(1); Padding = new Thickness(16);
        SetResourceReference(BackgroundProperty, "PanelBrush"); SetResourceReference(BorderBrushProperty, "BorderBrush");
        Focusable = true;
        var layout = new Grid(); Child = layout;
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });

        var header = new DockPanel { LastChildFill = true };
        var close = Ui.Tool("Cancel", Strings.T("OcrReturn"), Dismiss, "Esc"); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        var grip = new System.Windows.Controls.Primitives.Thumb { Width = 16, Height = 22, Cursor = Cursors.SizeAll, Margin = new Thickness(0, 0, 8, 0), ToolTip = Strings.T("OcrDrag") };
        var gripRoot = new FrameworkElementFactory(typeof(Border)); gripRoot.SetValue(BackgroundProperty, Brushes.Transparent);
        var gripDots = new FrameworkElementFactory(typeof(TextBlock)); gripDots.SetValue(TextBlock.TextProperty, "⋮"); gripDots.SetValue(TextBlock.FontSizeProperty, 21d);
        gripDots.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); gripDots.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); gripRoot.AppendChild(gripDots);
        grip.Template = new ControlTemplate(typeof(System.Windows.Controls.Primitives.Thumb)) { VisualTree = gripRoot };
        grip.Visibility = movable ? Visibility.Visible : Visibility.Collapsed;
        grip.DragDelta += (_, e) => DragRequested?.Invoke(new(e.HorizontalChange, e.VerticalChange));
        AutomationProperties.SetName(grip, Strings.T("OcrDrag")); DockPanel.SetDock(grip, Dock.Left); header.Children.Add(grip);
        var heading = new StackPanel(); heading.Children.Add(Ui.Label(Strings.T("Ocr"), 17)); heading.Children.Add(Ui.Label(Strings.T("OcrLocal"), 11, true)); header.Children.Add(heading);
        layout.Children.Add(header);
        var state = new StackPanel { Margin = new Thickness(0, 8, 0, 0) }; state.Children.Add(status); state.Children.Add(progress); Grid.SetRow(state, 1); layout.Children.Add(state);

        var choices = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var label = Ui.Label(Strings.T("OcrPunctuation"), 12); label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new Thickness(0, 0, 8, 0); choices.Children.Add(label);
        foreach (var mode in Enum.GetValues<OcrPunctuationMode>()) punctuation.Items.Add(new ComboBoxItem { Content = Strings.T("OcrMode" + mode), Tag = mode });
        punctuation.SelectedIndex = 0; AutomationProperties.SetName(punctuation, Strings.T("OcrPunctuation")); choices.Children.Add(punctuation);
        undo = Ui.Tool("Undo", Strings.T("Undo"), () => { result.Undo(); result.Focus(); }, "Ctrl+Z"); choices.Children.Add(undo);
        redo = Ui.Tool("Redo", Strings.T("Redo"), () => { result.Redo(); result.Focus(); }, "Ctrl+Y"); choices.Children.Add(redo);
        Grid.SetRow(choices, 2); layout.Children.Add(choices);

        ConfigureTextBox(result, Strings.T("OcrResult")); result.IsReadOnly = true;
        Grid.SetRow(result, 3); layout.Children.Add(result);
        review.Margin = new Thickness(0, 7, 0, 5); Grid.SetRow(review, 4); layout.Children.Add(review);
        ConfigureTextBox(original, Strings.T("OcrOriginal")); original.IsReadOnly = true; original.Height = 88;
        originalSection = new Expander { Header = Strings.T("OcrOriginal"), Content = original, Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed };
        Grid.SetRow(originalSection, 5); layout.Children.Add(originalSection);

        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        cancel = Ui.Button(Strings.T("OcrStop"), () => operation?.Cancel()); actions.Children.Add(cancel);
        retry = Ui.Tool("Reset", Strings.T("OcrRetry"), () => { if (snapshot != null) _ = RecognizeAsync(snapshot); }); actions.Children.Add(retry);
        save = Ui.Tool("Save", Strings.T("OcrSave"), SaveText); actions.Children.Add(save);
        copyOnly = Ui.Button("复制文字", () => _ = CopyAsync(false), "仅复制文字，保留当前窗口"); actions.Children.Add(copyOnly);
        copy = Ui.Action("Copy", Strings.T("OcrCopy"), () => _ = CopyAsync(true), true, "Ctrl+Enter"); actions.Children.Add(copy);
        Grid.SetRow(actions, 6); layout.Children.Add(actions);
        result.TextChanged += (_, _) => UpdateTextActions();
        punctuation.SelectionChanged += (_, _) => ApplyPunctuation();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; }
            else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { _ = CopyAsync(); e.Handled = true; }
        };
        SetBusy(false);
    }

    private static void ConfigureTextBox(TextBox box, string name)
    {
        box.AcceptsReturn = true; box.AcceptsTab = true; box.TextWrapping = TextWrapping.Wrap;
        box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; box.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        box.VerticalContentAlignment = VerticalAlignment.Top;
        box.FontFamily = new FontFamily("Microsoft YaHei UI"); box.FontSize = 14; box.Padding = new Thickness(9); box.UndoLimit = 100;
        InputMethod.SetIsInputMethodEnabled(box, true); AutomationProperties.SetName(box, name);
    }

    public async Task RecognizeAsync(EditDocument source)
    {
        Dispatcher.VerifyAccess();
        if (IsBusy || dismissed) return;
        snapshot = source;
        using var cts = new CancellationTokenSource(); operation = cts;
        result.Clear(); original.Clear(); rawText = ""; originalSection.Visibility = Visibility.Collapsed; review.Text = "";
        status.Text = Strings.T("OcrWorking"); SetBusy(true);
        bool acceptsProgress = true;
        var updates = new Progress<OcrProgress>(p =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!acceptsProgress || dismissed || !ReferenceEquals(operation, cts) || cts.IsCancellationRequested) return;
                status.Text = string.IsNullOrWhiteSpace(p.Message) ? Strings.T("OcrWorking") : p.Message;
                progress.IsIndeterminate = p.Total <= 0;
                if (p.Total > 0) { progress.Maximum = p.Total; progress.Value = p.Completed; }
            }));
        });
        try
        {
            var recognized = await OcrService.RecognizeAsync(source, cts.Token, updates);
            cts.Token.ThrowIfCancellationRequested();
            await OnUiAsync(() =>
            {
                acceptsProgress = false;
                cts.Token.ThrowIfCancellationRequested();
                if (!dismissed) SetRecognition(recognized);
            });
        }
        catch (OperationCanceledException) { await OnUiAsync(() => { acceptsProgress = false; if (!dismissed) status.Text = Strings.T("OcrCancelled"); }); }
        catch (Exception error)
        {
            // OCR text and source image are never written to logs.
            Log.Error("OCR failure", error);
            await OnUiAsync(() => { acceptsProgress = false; if (!dismissed) { status.Text = Strings.T("OcrFailed"); review.Text = error.Message; } });
        }
        finally
        {
            await OnUiAsync(() =>
            {
                acceptsProgress = false;
                if (ReferenceEquals(operation, cts)) operation = null;
                if (!dismissed) SetBusy(false);
            });
        }
    }
    private async Task OnUiAsync(Action action)
    {
        if (Dispatcher.CheckAccess()) { action(); return; }
        if (Dispatcher.HasShutdownStarted) return;
        try { await Dispatcher.InvokeAsync(action); }
        catch (TaskCanceledException) when (dismissed || Dispatcher.HasShutdownStarted) { }
    }

    internal void SetRecognition(OcrRecognition recognized)
    {
        rawText = string.Join(Environment.NewLine, recognized.Lines.Select(line => line.Text)); original.Text = rawText;
        updating = true;
        try { result.Text = OcrPunctuation.Format(rawText, SelectedMode); } finally { updating = false; }
        // The recognized text is the initial document, not a user edit to undo.
        result.IsUndoEnabled = false; result.IsUndoEnabled = true; UpdateTextActions();
        originalSection.Visibility = rawText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var lowIndices = recognized.Lines
            .Select((line, index) => (line.Confidence, Number: index + 1))
            .Where(item => item.Confidence < .85)
            .Select(item => item.Number)
            .ToArray();
        int uncertain = lowIndices.Length;
        status.Text = recognized.Lines.Count == 0 ? Strings.T("OcrEmpty") : string.Format(CultureInfo.CurrentCulture, Strings.T("OcrComplete"), recognized.Lines.Count, recognized.ElapsedMilliseconds / 1000d);
        if (uncertain > 0)
        {
            string lineList = string.Join("、", lowIndices.Take(6)) + (lowIndices.Length > 6 ? " 等" : "");
            review.Text = $"有 {uncertain} 行（第 {lineList} 行）置信度较低，请对照截图核对文字、标点及易混字符。";
        }
        else review.Text = Strings.T("OcrReviewHint");
        if (rawText.Length > 0) { result.Focus(); result.CaretIndex = 0; }
    }

    private OcrPunctuationMode SelectedMode => punctuation.SelectedItem is ComboBoxItem { Tag: OcrPunctuationMode mode } ? mode : OcrPunctuationMode.Smart;
    private void ApplyPunctuation()
    {
        if (updating || IsBusy || rawText.Length == 0) return;
        string next = OcrPunctuation.Format(SelectedMode == OcrPunctuationMode.Original ? rawText : result.Text, SelectedMode);
        if (next == result.Text) return;
        // Treat a formatting change like one text edit, preserving manual fixes
        // and native Undo/Redo. Original mode can also be undone.
        result.BeginChange();
        try { result.SelectAll(); result.SelectedText = next; result.CaretIndex = 0; }
        finally { result.EndChange(); }
    }
    private void SetBusy(bool busy)
    {
        progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; progress.IsIndeterminate = busy;
        cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; retry.IsEnabled = !busy && rawText.Length == 0;
        punctuation.IsEnabled = !busy; result.IsReadOnly = busy;
        UpdateTextActions();
    }
    private void UpdateTextActions()
    {
        copyOnly.IsEnabled = copy.IsEnabled = save.IsEnabled = !IsBusy && result.Text.Length > 0;
        undo.IsEnabled = !IsBusy && result.CanUndo;
        redo.IsEnabled = !IsBusy && result.CanRedo;
    }
    private async Task CopyAsync(bool complete = true)
    {
        if (IsBusy || result.Text.Length == 0 || dismissed) return;
        string text = result.Text;
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                if (dismissed) return;
                try { Clipboard.SetText(text); break; }
                catch (System.Runtime.InteropServices.COMException) when (attempt < 4) { await Task.Delay(60); }
            }
            status.Text = Strings.T("OcrCopied");
            if (complete) CopyCompleted?.Invoke();
        }
        catch (Exception error) { status.Text = Strings.T("OcrCopyFailed"); Log.Error("OCR clipboard", error); }
    }
    private void SaveText()
    {
        if (IsBusy || result.Text.Length == 0) return;
        var picker = new Microsoft.Win32.SaveFileDialog { FileName = $"Text_{DateTime.Now:yyyy-MM-dd_HHmmss}", DefaultExt = ".txt", Filter = Strings.T("OcrTextFilter") };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            AtomicFile.Write(picker.FileName, stream => { using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true); writer.Write(result.Text); });
            status.Text = Strings.T("OcrSaved");
        }
        catch (Exception error) { status.Text = Strings.T("OcrSaveFailed"); Log.Error("OCR save", error); }
    }
    public void Dismiss()
    {
        if (dismissed) return;
        dismissed = true; operation?.Cancel(); snapshot = null; CloseRequested?.Invoke();
    }
}
