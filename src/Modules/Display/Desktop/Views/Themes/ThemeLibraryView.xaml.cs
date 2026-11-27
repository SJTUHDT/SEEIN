using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using ListBox = System.Windows.Controls.ListBox;

namespace Seein.Tray;

public partial class ThemeLibraryView : System.Windows.Controls.UserControl
{
    private ThemeEditorView? _editor;
    private readonly Grid _pages = new();
    private readonly System.Windows.Controls.RadioButton _editorTab = new() { Content = "主题编辑器", GroupName = "theme-library", Margin = new(0,0,8,0) };
    private UIElement _library = null!;
    public ThemeEditorView? ExistingEditor => _editor;
    public event Action<ThemeEditorView>? EditorCreated;
    public event Action? EditRequested;
    public ThemeLibraryView()
    {
        InitializeComponent();_library=(UIElement)Content;Content=null;
        var root=new Grid();root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new());
        var header=new StackPanel{Orientation=System.Windows.Controls.Orientation.Horizontal,Margin=new(0,0,0,14)};
        var libraryTab=new System.Windows.Controls.RadioButton{Content="我的主题",GroupName="theme-library",IsChecked=true,Margin=new(0,0,8,0)};
        libraryTab.SetResourceReference(StyleProperty,"SegmentedButtonStyle");_editorTab.SetResourceReference(StyleProperty,"SegmentedButtonStyle");
        header.Children.Add(libraryTab);header.Children.Add(_editorTab);
        var create=new Button{Content="新建主题",Margin=new(16,0,8,0)};create.Click+=(_,_)=>{OpenEditor();_editor!.NewDocument();};header.Children.Add(create);
        var edit=new Button{Content="编辑所选主题",ToolTip="内置和导入主题会创建副本；自己的工程直接编辑"};edit.Click+=(_,_)=>EditRequested?.Invoke();header.Children.Add(edit);
        libraryTab.Checked+=(_,_)=>{_library.Visibility=Visibility.Visible;if(_editor is not null)_editor.Visibility=Visibility.Collapsed;};
        _editorTab.Checked+=(_,_)=>ShowEditor();
        root.Children.Add(header);_pages.Children.Add(_library);Grid.SetRow(_pages,1);root.Children.Add(_pages);Content=root;
    }
    private void ShowEditor()
    {
        if(_editor is null){_editor=new();_pages.Children.Add(_editor);EditorCreated?.Invoke(_editor);}
        _library.Visibility=Visibility.Collapsed;_editor.Visibility=Visibility.Visible;
    }
    public void OpenEditor(string? directory=null,bool copy=true)
    { _editorTab.IsChecked=true;ShowEditor();if(directory is not null)_editor!.OpenTheme(directory,copy); }

    public ListBox ThemeList => ImportedThemesList;
    public Button ApplyButton => EnableSelectedThemeButton;
    public Button DeleteButton => DeleteSelectedThemeButton;
    public Image PreviewImageControl => PreviewImage;
    public TextBlock PreviewHintControl => PreviewHint;

    public event RoutedEventHandler? ImportRequested;
    public event RoutedEventHandler? ApplyRequested;
    public event RoutedEventHandler? DeleteRequested;
    public event SelectionChangedEventHandler? SelectionChanged;

    private void ImportTheme_Click(object sender, RoutedEventArgs e) => ImportRequested?.Invoke(sender, e);
    private void EnableSelectedTheme_Click(object sender, RoutedEventArgs e) => ApplyRequested?.Invoke(sender, e);
    private void DeleteSelectedTheme_Click(object sender, RoutedEventArgs e) => DeleteRequested?.Invoke(sender, e);
    private void ImportedThemesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => SelectionChanged?.Invoke(sender, e);
}
