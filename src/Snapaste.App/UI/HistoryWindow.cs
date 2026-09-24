using Snapaste.Core;
using Snapaste.Editor;
namespace Snapaste.UI;
public sealed class HistoryWindow : Window
{
    public HistoryWindow(HistoryStore store)
    {
        Title = "Snapaste · 截图历史"; Width = 820; Height = 660; Ui.Setup(this);
        var items = new WrapPanel { Margin = new Thickness(12) }; Content = new ScrollViewer { Content = items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        foreach (var entry in store.Entries())
        {
            var box = new StackPanel { Width = 230, Margin = new Thickness(10), Background = Brushes.White };
            box.SetResourceReference(Panel.BackgroundProperty, "PanelBrush");
            box.Children.Add(new Image { Source = ImageDocument.ReadBitmap(entry.Thumbnail), Height = 160, Stretch = Stretch.Uniform });
            box.Children.Add(new TextBlock { Text = $"{entry.Created:MM-dd HH:mm:ss} · {(entry.Kind == "Scroll" ? "滚动" : "普通")}\n{entry.Width} × {entry.Height}", Margin = new Thickness(8) });
            var actions = new WrapPanel();
            actions.Children.Add(Ui.Button(Strings.T("Copy"), async () => { try { await ImageExport.Copy(new(entry.Open())); } catch (Exception e) { Ui.Error(e); } }));
            actions.Children.Add(Ui.Button(Strings.T("Save"), () => ImageExport.SaveDialog(new(entry.Open()), App.Current.Settings, this)));
            actions.Children.Add(Ui.Button(Strings.T("Pin"), () => App.Current.PinDocument(new(entry.Open()))));
            actions.Children.Add(Ui.Button(Strings.T("Edit"), () => App.Current.Edit(entry.Open()))); box.Children.Add(actions); items.Children.Add(box);
        }
        if (items.Children.Count == 0) items.Children.Add(new TextBlock { Text = "完成的截图将保存在这里。", Margin = new Thickness(24) });
    }
}
