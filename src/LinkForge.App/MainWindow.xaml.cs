namespace LinkForge.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
/// <remarks>
/// 与 <see cref="App"/> 同理，基类需写全限定名以避开 <c>LinkForge.Application</c> 命名空间。
/// </remarks>
public partial class MainWindow : System.Windows.Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
