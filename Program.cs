using System.Windows;
using Velopack;

namespace ThoughtCanvas;

// 진입점 — Velopack은 설치·업데이트·제거 때 앱을 특수 인자로 잠깐 실행하므로,
// WPF가 창을 만들기 전에 VelopackApp.Run()이 가장 먼저 처리해야 합니다.
// (App.xaml이 자동 생성하는 Main 대신 csproj의 StartupObject로 이 Main을 씁니다)
// --self-test 등 일반 인자는 그대로 App으로 전달됩니다.
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
