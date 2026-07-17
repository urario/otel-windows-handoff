using Microsoft.UI.Xaml;

namespace OtelWindowsHandoff.WinUI;

/// <summary>
/// WinUI アプリケーションの起動とメインウィンドウの所有を担当します。
/// パイプライン処理はウィンドウから Core へ委譲し、起動クラスへ業務処理を持ち込みません。
/// </summary>
public partial class App : Application
{
    private Window? window;

    /// <summary>WinUI が生成した XAML リソースを初期化します。</summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>コマンドライン引数を保持したメインウィンドウを作成して表示します。</summary>
    /// <param name="args">WinUI から渡される起動情報。</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] commandLineArguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        window = new MainWindow(commandLineArguments);
        window.Activate();
    }
}
